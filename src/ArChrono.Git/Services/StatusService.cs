using ArChrono.Git.Models;
using ArChrono.Git.Parsing;
using ArChrono.Git.Process;

namespace ArChrono.Git.Services;

public sealed class StatusService(IGitRunner runner, RepositoryInfo repository) : GitServiceBase(runner, repository)
{
    public async Task<RepositoryStatus> GetStatusAsync(CancellationToken cancellationToken = default, bool isInternal = false)
    {
        var result = await ExecuteAsync(
            ["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all"],
            cancellationToken, readOnly: true, isInternal: isInternal).ConfigureAwait(false);
        var (branch, entries) = StatusParser.Parse(result.EnsureSuccess().StandardOutput);
        return new RepositoryStatus(branch, entries, GetState());
    }

    /// <summary>Yarım kalmış işlemi .git dizinindeki durum dosyalarından tespit eder (süreç başlatmaz).</summary>
    public RepositoryState GetState()
    {
        var gitDir = Repository.GitDir;
        if (Directory.Exists(Path.Combine(gitDir, "rebase-merge"))) return RepositoryState.Rebasing;
        if (Directory.Exists(Path.Combine(gitDir, "rebase-apply")))
            return File.Exists(Path.Combine(gitDir, "rebase-apply", "applying")) ? RepositoryState.ApplyingPatches : RepositoryState.Rebasing;
        if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD"))) return RepositoryState.Merging;
        if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD"))) return RepositoryState.CherryPicking;
        if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD"))) return RepositoryState.Reverting;
        if (File.Exists(Path.Combine(gitDir, "BISECT_LOG"))) return RepositoryState.Bisecting;
        return RepositoryState.Clean;
    }
}
