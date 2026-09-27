using System.Text;
using ArChrono.Git.Models;
using ArChrono.Git.Parsing;
using ArChrono.Git.Process;

namespace ArChrono.Git.Services;

/// <summary>Plumbing: nesne okuma/yazma, ağaçlar, index. Snapshot engine ve restore bu servisi kullanır.</summary>
public sealed class ObjectService(IGitRunner runner, RepositoryInfo repository) : GitServiceBase(runner, repository)
{
    /// <summary>Uygulamanın kendi nesneleri (pin commit) için sabit kimlik; kullanıcının user.name ayarına bağımlı değildir.</summary>
    private static readonly IReadOnlyDictionary<string, string> InternalIdentity = new Dictionary<string, string>
    {
        ["GIT_AUTHOR_NAME"] = "ArChrono",
        ["GIT_AUTHOR_EMAIL"] = "recovery@archrono.local",
        ["GIT_COMMITTER_NAME"] = "ArChrono",
        ["GIT_COMMITTER_EMAIL"] = "recovery@archrono.local",
    };

    /// <summary>Blob içeriği. <paramref name="objectSpec"/>: sha, "tree:path" veya ":stage:path". Yoksa null.</summary>
    public async Task<byte[]?> ReadBlobAsync(string objectSpec, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(["cat-file", "blob", objectSpec], cancellationToken, readOnly: true, isInternal: true).ConfigureAwait(false);
        return result.Success ? result.StandardOutputBytes : null;
    }

    public async Task<bool> ObjectExistsAsync(string sha, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(["cat-file", "-e", sha], cancellationToken, readOnly: true, isInternal: true).ConfigureAwait(false);
        return result.Success;
    }

    public async Task<IReadOnlyList<TreeEntry>> ListTreeAsync(string treeish, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(["ls-tree", "-r", "-z", treeish], cancellationToken, readOnly: true, isInternal: true).ConfigureAwait(false);
        return TreeParser.ParseLsTree(result.EnsureSuccess().StandardOutput);
    }

    public async Task<IReadOnlyList<IndexEntry>> ListIndexAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(["ls-files", "-s", "-z"], cancellationToken, readOnly: true, isInternal: true).ConfigureAwait(false);
        return TreeParser.ParseLsFilesStage(result.EnsureSuccess().StandardOutput);
    }

    public async Task<string> GetEmptyTreeAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(["mktree"], cancellationToken, standardInput: [], isInternal: true).ConfigureAwait(false);
        return result.EnsureSuccess().StandardOutput.Trim();
    }

    /// <summary>
    /// Index'in kopyası üzerinden ağaç yazar; kullanıcının gerçek index dosyasına ve index.lock'a dokunulmaz.
    /// Unmerged girdiler varsa <c>null</c> döner.
    /// </summary>
    public async Task<string?> WriteTreeFromIndexCopyAsync(CancellationToken cancellationToken = default)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "archrono-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var tempIndex = Path.Combine(tempDirectory, "index");
            if (File.Exists(Repository.IndexFilePath))
            {
                CopyShared(Repository.IndexFilePath, tempIndex);
                // Split index kullanan repository'lerde paylaşılan index dosyaları da gerekir.
                foreach (var shared in Directory.EnumerateFiles(Repository.GitDir, "sharedindex.*"))
                    CopyShared(shared, Path.Combine(tempDirectory, Path.GetFileName(shared)));
            }

            var environment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = tempIndex };
            var result = await ExecuteAsync(["write-tree"], cancellationToken, environment: environment, isInternal: true).ConfigureAwait(false);
            if (result.Success) return result.StandardOutput.Trim();
            if (result.StandardError.Contains("unmerged", StringComparison.OrdinalIgnoreCase)) return null;
            result.EnsureSuccess();
            return null;
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    public async Task<string> CommitTreeAsync(string tree, IEnumerable<string> parents, string message, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "commit-tree", tree };
        foreach (var parent in parents)
        {
            args.Add("-p");
            args.Add(parent);
        }
        args.Add("-F");
        args.Add("-");
        var result = await ExecuteAsync(args, cancellationToken, standardInput: Encoding.UTF8.GetBytes(message), environment: InternalIdentity, isInternal: true).ConfigureAwait(false);
        return result.EnsureSuccess().StandardOutput.Trim();
    }

    /// <summary><c>read-tree --reset</c>: index'i ağaçla değiştirir; eşleşen girdilerin stat bilgisi korunur.</summary>
    public async Task ReadTreeIntoIndexAsync(string tree, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(["read-tree", "--reset", tree], cancellationToken, isInternal: true).ConfigureAwait(false);
        result.EnsureSuccess();
    }

    /// <summary>Verilen yolları index'teki içerikle çalışma alanına yazar (filtreler/eol uygulanır).</summary>
    public async Task CheckoutIndexAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0) return;
        var result = await ExecuteAsync(["checkout-index", "-f", "-z", "--stdin"], cancellationToken, standardInput: NulJoined(paths), isInternal: true).ConfigureAwait(false);
        result.EnsureSuccess();
    }

    public async Task RefreshIndexAsync(CancellationToken cancellationToken = default) =>
        await ExecuteAsync(["update-index", "-q", "--refresh"], cancellationToken, isInternal: true).ConfigureAwait(false);

    /// <summary>
    /// Hiçbir ref veya reflog tarafından erişilemeyen "dangling" commit'ler — kayıp commit ve stash adayları.
    /// Büyük repository'lerde yavaş olabilir.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindDanglingCommitsAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(["fsck", "--connectivity-only", "--no-reflogs", "--no-progress"], cancellationToken,
            readOnly: true, timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        const string prefix = "dangling commit ";
        return result.StandardOutput.Split('\n')
            .Where(l => l.StartsWith(prefix, StringComparison.Ordinal))
            .Select(l => l[prefix.Length..].Trim())
            .ToList();
    }

    private static void CopyShared(string source, string destination)
    {
        // Git dosyayı yazarken okunabilmesi için paylaşımlı açılır.
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class ConflictService(IGitRunner runner, RepositoryInfo repository, ObjectService objects) : GitServiceBase(runner, repository)
{
    public async Task<IReadOnlyList<ConflictFile>> GetConflictsAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(["ls-files", "-u", "-z"], cancellationToken, readOnly: true).ConfigureAwait(false);
        var entries = TreeParser.ParseLsFilesStage(result.EnsureSuccess().StandardOutput);
        return entries.GroupBy(e => e.Path, StringComparer.Ordinal)
            .Select(g => new ConflictFile(g.Key,
                g.FirstOrDefault(e => e.Stage == 1),
                g.FirstOrDefault(e => e.Stage == 2),
                g.FirstOrDefault(e => e.Stage == 3)))
            .OrderBy(c => c.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>1 = base, 2 = ours (current), 3 = theirs (incoming).</summary>
    public Task<byte[]?> ReadStageAsync(string path, int stage, CancellationToken cancellationToken = default) =>
        objects.ReadBlobAsync($":{stage}:{path}", cancellationToken);

    public async Task<GitCommandResult> ResolveWithSideAsync(ConflictFile conflict, bool useOurs, CancellationToken cancellationToken = default)
    {
        var side = useOurs ? conflict.Ours : conflict.Theirs;
        if (side is null)
            return await ExecuteAsync(["rm", "-q", "--", conflict.Path], cancellationToken, environment: LiteralPathspecs).ConfigureAwait(false);

        var checkout = await ExecuteAsync(["checkout", useOurs ? "--ours" : "--theirs", "--", conflict.Path], cancellationToken, environment: LiteralPathspecs).ConfigureAwait(false);
        if (!checkout.Success) return checkout;
        return await ExecuteAsync(["add", "--", conflict.Path], cancellationToken, environment: LiteralPathspecs).ConfigureAwait(false);
    }

    public async Task<GitCommandResult> SaveResolutionAsync(string path, byte[] content, CancellationToken cancellationToken = default)
    {
        await File.WriteAllBytesAsync(Path.Combine(Repository.RootPath, path), content, cancellationToken).ConfigureAwait(false);
        return await ExecuteAsync(["add", "--", path], cancellationToken, environment: LiteralPathspecs).ConfigureAwait(false);
    }
}
