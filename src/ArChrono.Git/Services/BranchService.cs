using ArChrono.Git.Models;
using ArChrono.Git.Process;

namespace ArChrono.Git.Services;

public sealed class BranchService(IGitRunner runner, RepositoryInfo repository) : GitServiceBase(runner, repository)
{
    public Task<GitCommandResult> CreateAsync(string name, string? startPoint, bool checkout, CancellationToken cancellationToken = default)
    {
        var args = checkout ? new List<string> { "switch", "-c", name } : ["branch", name];
        if (!string.IsNullOrEmpty(startPoint)) args.Add(startPoint);
        return ExecuteAsync(args, cancellationToken);
    }

    public Task<GitCommandResult> DeleteAsync(string name, bool force, CancellationToken cancellationToken = default) =>
        ExecuteAsync(["branch", force ? "-D" : "-d", "--", RefNames.Shorten(name)], cancellationToken);

    public Task<GitCommandResult> RenameAsync(string oldName, string newName, CancellationToken cancellationToken = default) =>
        ExecuteAsync(["branch", "-m", RefNames.Shorten(oldName), newName], cancellationToken);

    public Task<GitCommandResult> SwitchAsync(string name, CancellationToken cancellationToken = default) =>
        ExecuteAsync(["switch", "--no-guess", RefNames.Shorten(name)], cancellationToken);

    /// <summary>Uzak branch'i yerel izleyen branch oluşturarak açar (origin/feature → feature).</summary>
    public Task<GitCommandResult> SwitchToRemoteAsync(string remoteBranch, string localName, CancellationToken cancellationToken = default) =>
        ExecuteAsync(["switch", "-c", localName, "--track", RefNames.Shorten(remoteBranch)], cancellationToken);

    public Task<GitCommandResult> SwitchDetachedAsync(string revision, CancellationToken cancellationToken = default) =>
        ExecuteAsync(["switch", "--detach", revision], cancellationToken);

    public Task<GitCommandResult> SetUpstreamAsync(string branch, string upstream, CancellationToken cancellationToken = default) =>
        ExecuteAsync(["branch", "--set-upstream-to=" + upstream, RefNames.Shorten(branch)], cancellationToken);
}
