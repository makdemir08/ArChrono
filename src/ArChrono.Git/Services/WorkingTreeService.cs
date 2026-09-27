using System.Text;
using ArChrono.Git.Models;
using ArChrono.Git.Process;

namespace ArChrono.Git.Services;

/// <summary>Stage/unstage, değişiklikleri atma ve commit.</summary>
public sealed class WorkingTreeService(IGitRunner runner, RepositoryInfo repository) : GitServiceBase(runner, repository)
{
    public Task<GitCommandResult> StageAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default) =>
        ExecuteAsync(["add", "-A", "--pathspec-from-file=-", "--pathspec-file-nul"], cancellationToken,
            standardInput: NulJoined(paths), environment: LiteralPathspecs);

    public Task<GitCommandResult> StageAllAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(["add", "-A"], cancellationToken);

    public async Task<GitCommandResult> UnstageAsync(IReadOnlyCollection<string> paths, bool headExists, CancellationToken cancellationToken = default)
    {
        string[] args = headExists
            ? ["restore", "--staged", "--pathspec-from-file=-", "--pathspec-file-nul"]
            : ["rm", "--cached", "-r", "-q", "--pathspec-from-file=-", "--pathspec-file-nul"];
        return await ExecuteAsync(args, cancellationToken, standardInput: NulJoined(paths), environment: LiteralPathspecs).ConfigureAwait(false);
    }

    public Task<GitCommandResult> UnstageAllAsync(bool headExists, CancellationToken cancellationToken = default) =>
        headExists
            ? ExecuteAsync(["reset", "-q", "HEAD", "--", "."], cancellationToken)
            : ExecuteAsync(["rm", "--cached", "-r", "-q", "--", "."], cancellationToken);

    /// <summary>Takip edilen dosyalardaki stage edilmemiş değişiklikleri index sürümüne döndürür.</summary>
    public Task<GitCommandResult> RestoreWorktreeAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default) =>
        ExecuteAsync(["restore", "--worktree", "--pathspec-from-file=-", "--pathspec-file-nul"], cancellationToken,
            standardInput: NulJoined(paths), environment: LiteralPathspecs);

    /// <summary>Dosyaları HEAD sürümüne döndürür (index ve çalışma alanı).</summary>
    public Task<GitCommandResult> RestoreFromHeadAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default) =>
        ExecuteAsync(["restore", "--source=HEAD", "--staged", "--worktree", "--pathspec-from-file=-", "--pathspec-file-nul"], cancellationToken,
            standardInput: NulJoined(paths), environment: LiteralPathspecs);

    /// <summary>Untracked dosyaları siler. Yalnızca repository içindeki dosyalar; ignore edilmiş dosyalara dokunmaz.</summary>
    public IReadOnlyList<string> DeleteUntrackedFiles(IEnumerable<string> relativePaths)
    {
        var root = Path.GetFullPath(Repository.RootPath);
        var deleted = new List<string>();
        foreach (var relative in relativePaths)
        {
            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(root, StringComparison.Ordinal)) continue;
            if (File.Exists(full) || IsSymlink(full))
            {
                File.Delete(full);
                deleted.Add(relative);
                PruneEmptyDirectories(Path.GetDirectoryName(full), root);
            }
        }
        return deleted;
    }

    public Task<GitCommandResult> ApplyPatchAsync(string patch, bool toIndex, bool reverse, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "apply", "--whitespace=nowarn", "--recount" };
        if (toIndex) args.Add("--cached");
        if (reverse) args.Add("--reverse");
        args.Add("-");
        return ExecuteAsync(args, cancellationToken, standardInput: Encoding.UTF8.GetBytes(patch));
    }

    public Task<GitCommandResult> CommitAsync(string message, bool amend, bool allowEmpty = false, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "commit", "-F", "-" };
        if (amend) args.Add("--amend");
        if (allowEmpty) args.Add("--allow-empty");
        return ExecuteAsync(args, cancellationToken, standardInput: Encoding.UTF8.GetBytes(message));
    }

    public async Task<string> GetHeadMessageAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(["log", "-1", "--format=%B", "HEAD", "--"], cancellationToken, readOnly: true).ConfigureAwait(false);
        return result.Success ? result.StandardOutput.TrimEnd() : string.Empty;
    }

    private static bool IsSymlink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (IOException)
        {
            return false;
        }
    }

    internal static void PruneEmptyDirectories(string? directory, string root)
    {
        while (directory is not null && directory.Length > root.Length &&
               directory.StartsWith(root, StringComparison.Ordinal) && Directory.Exists(directory) &&
               !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }
}
