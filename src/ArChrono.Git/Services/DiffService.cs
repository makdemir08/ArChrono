using ArChrono.Git.Diffing;
using ArChrono.Git.Models;
using ArChrono.Git.Parsing;
using ArChrono.Git.Process;

namespace ArChrono.Git.Services;

public sealed class DiffService(IGitRunner runner, RepositoryInfo repository) : GitServiceBase(runner, repository)
{
    private static readonly string[] CommonArgs = ["--no-color", "--no-ext-diff", "--src-prefix=a/", "--dst-prefix=b/", "-M"];

    /// <summary>Çalışma alanı ↔ index.</summary>
    public Task<IReadOnlyList<FileDiff>> GetUnstagedAsync(string? path = null, int context = 3, CancellationToken cancellationToken = default) =>
        RunDiffAsync(Args(["diff"], CommonArgs, [$"-U{context}"]), path, cancellationToken);

    /// <summary>Index ↔ HEAD.</summary>
    public Task<IReadOnlyList<FileDiff>> GetStagedAsync(string? path = null, int context = 3, CancellationToken cancellationToken = default) =>
        RunDiffAsync(Args(["diff", "--cached"], CommonArgs, [$"-U{context}"]), path, cancellationToken);

    /// <summary>AI bağlamı için stage edilmiş değişikliklerin ham patch metni.</summary>
    public async Task<string> GetStagedPatchAsync(int context = 3, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(Args(["diff", "--cached"], CommonArgs, [$"-U{context}"]), cancellationToken, readOnly: true).ConfigureAwait(false);
        return result.EnsureSuccess().StandardOutput;
    }

    public async Task<string> GetCommitPatchAsync(CommitInfo commit, int context = 3, CancellationToken cancellationToken = default)
    {
        var args = commit.Parents.Count > 0
            ? Args(["diff"], CommonArgs, [$"-U{context}", commit.Parents[0], commit.Sha])
            : Args(["show", "--format="], CommonArgs, [$"-U{context}", commit.Sha]);
        var result = await ExecuteAsync(args, cancellationToken, readOnly: true).ConfigureAwait(false);
        return result.EnsureSuccess().StandardOutput;
    }

    public Task<IReadOnlyList<FileDiff>> GetCommitDiffAsync(CommitInfo commit, string? path = null, int context = 3, CancellationToken cancellationToken = default)
    {
        // Merge commit'lerde ilk parent'a göre diff (kullanıcının branch'ine ne geldiği).
        var args = commit.Parents.Count > 0
            ? Args(["diff"], CommonArgs, [$"-U{context}", commit.Parents[0], commit.Sha])
            : Args(["show", "--format="], CommonArgs, [$"-U{context}", commit.Sha]);
        return RunDiffAsync(args, path, cancellationToken);
    }

    public Task<IReadOnlyList<FileDiff>> GetDiffBetweenAsync(string fromRevision, string toRevision, string? path = null, int context = 3, CancellationToken cancellationToken = default) =>
        RunDiffAsync(Args(["diff"], CommonArgs, [$"-U{context}", fromRevision, toRevision]), path, cancellationToken);

    /// <summary>Untracked dosya için "tamamı eklendi" diff'i (git'e süreç açmadan).</summary>
    public FileDiff GetUntrackedFileDiff(string relativePath)
    {
        var full = Path.Combine(Repository.RootPath, relativePath);
        byte[]? content = null;
        try
        {
            var info = new FileInfo(full);
            if (info.LinkTarget is { } target) content = System.Text.Encoding.UTF8.GetBytes(target);
            else if (info.Exists && info.Length <= 5 * 1024 * 1024) content = File.ReadAllBytes(full);
            else if (info.Exists) return new FileDiff(null, relativePath, ChangeKind.Added, true, null, null, []);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return TextDiff.Compare(null, relativePath, null, content ?? []);
    }

    private async Task<IReadOnlyList<FileDiff>> RunDiffAsync(string[] args, string? path, CancellationToken cancellationToken)
    {
        var fullArgs = path is null ? args : Args(args, ["--", path]);
        var result = await ExecuteAsync(fullArgs, cancellationToken, readOnly: true, environment: LiteralPathspecs).ConfigureAwait(false);
        return DiffParser.Parse(result.EnsureSuccess().StandardOutput);
    }
}
