using System.Text;
using ArChrono.Git.Models;
using ArChrono.Git.Parsing;
using ArChrono.Git.Process;

namespace ArChrono.Git.Services;

public sealed class StashService(IGitRunner runner, RepositoryInfo repository) : GitServiceBase(runner, repository)
{
    public async Task<IReadOnlyList<StashEntry>> ListAsync(CancellationToken cancellationToken = default, bool isInternal = false)
    {
        var result = await ExecuteAsync(["stash", "list", "--format=" + StashParser.Format], cancellationToken, readOnly: true, isInternal: isInternal).ConfigureAwait(false);
        return result.Success ? StashParser.Parse(result.StandardOutput) : [];
    }

    public Task<GitCommandResult> PushAsync(string? message, bool includeUntracked, bool keepIndex = false, IReadOnlyCollection<string>? paths = null, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "stash", "push" };
        if (includeUntracked) args.Add("--include-untracked");
        if (keepIndex) args.Add("--keep-index");
        if (!string.IsNullOrWhiteSpace(message))
        {
            args.Add("-m");
            args.Add(message);
        }
        if (paths is { Count: > 0 })
        {
            // GIT_LITERAL_PATHSPECS stash'in iç ":/" pathspec'ini bozar; yollar tek tek literal işaretlenir.
            args.Add("--");
            args.AddRange(paths.Select(p => ":(literal)" + p));
        }
        return ExecuteAsync(args, cancellationToken);
    }

    public Task<GitCommandResult> ApplyAsync(int index, bool restoreIndex = false, CancellationToken cancellationToken = default) =>
        ExecuteAsync(restoreIndex ? ["stash", "apply", "--index", $"stash@{{{index}}}"] : ["stash", "apply", $"stash@{{{index}}}"], cancellationToken);

    public Task<GitCommandResult> PopAsync(int index, bool restoreIndex = false, CancellationToken cancellationToken = default) =>
        ExecuteAsync(restoreIndex ? ["stash", "pop", "--index", $"stash@{{{index}}}"] : ["stash", "pop", $"stash@{{{index}}}"], cancellationToken);

    public Task<GitCommandResult> DropAsync(int index, CancellationToken cancellationToken = default) =>
        ExecuteAsync(["stash", "drop", $"stash@{{{index}}}"], cancellationToken);

    /// <summary>Stash commit'ini listeye (en üste) geri ekler — stash drop'u geri almak için.</summary>
    public Task<GitCommandResult> StoreAsync(string sha, string message, CancellationToken cancellationToken = default) =>
        ExecuteAsync(["stash", "store", "-m", message, sha], cancellationToken);
}

public sealed class ReflogService(IGitRunner runner, RepositoryInfo repository) : GitServiceBase(runner, repository)
{
    public async Task<IReadOnlyList<ReflogEntry>> GetAsync(string refName = "HEAD", int maxCount = 500, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(["reflog", "show", "--date=unix", "--format=" + ReflogParser.Format, $"-n{maxCount}", refName, "--"],
            cancellationToken, readOnly: true).ConfigureAwait(false);
        return result.Success ? ReflogParser.Parse(result.StandardOutput, refName) : [];
    }
}

public enum PullMode
{
    /// <summary>Kullanıcının pull.rebase ayarına uyar; ayar yoksa merge.</summary>
    Default,
    Merge,
    Rebase,
    FastForwardOnly,
}

public sealed class RemoteService(IGitRunner runner, RepositoryInfo repository) : GitServiceBase(runner, repository)
{
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(30);

    public async Task<IReadOnlyList<RemoteInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var output = await ReadAsync(cancellationToken, "remote", "-v").ConfigureAwait(false);
        var fetch = new Dictionary<string, string>();
        var push = new Dictionary<string, string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var name = line[..tab];
            var rest = line[(tab + 1)..];
            var space = rest.LastIndexOf(' ');
            if (space < 0) continue;
            var url = rest[..space];
            if (rest.EndsWith("(push)", StringComparison.Ordinal)) push[name] = url;
            else fetch[name] = url;
        }
        return fetch.Keys.Union(push.Keys)
            .Select(name => new RemoteInfo(name, fetch.GetValueOrDefault(name, ""), push.GetValueOrDefault(name, fetch.GetValueOrDefault(name, ""))))
            .ToList();
    }

    public Task<GitCommandResult> AddAsync(string name, string url, CancellationToken cancellationToken = default) =>
        ExecuteAsync(["remote", "add", name, url], cancellationToken);

    public Task<GitCommandResult> RemoveAsync(string name, CancellationToken cancellationToken = default) =>
        ExecuteAsync(["remote", "remove", name], cancellationToken);

    public Task<GitCommandResult> RenameAsync(string oldName, string newName, CancellationToken cancellationToken = default) =>
        ExecuteAsync(["remote", "rename", oldName, newName], cancellationToken);

    public Task<GitCommandResult> SetUrlAsync(string name, string url, CancellationToken cancellationToken = default) =>
        ExecuteAsync(["remote", "set-url", name, url], cancellationToken);

    public Task<GitCommandResult> FetchAsync(string? remote = null, bool prune = true, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "fetch", "--progress" };
        if (prune) args.Add("--prune");
        args.Add(remote ?? "--all");
        return ExecuteAsync(args, cancellationToken, progress: progress, timeout: NetworkTimeout);
    }

    public Task<GitCommandResult> PullAsync(PullMode mode, string? remote = null, string? branch = null, bool autostash = false, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "pull", "--progress", "--no-edit" };
        switch (mode)
        {
            case PullMode.Merge: args.Add("--no-rebase"); break;
            case PullMode.Rebase: args.Add("--rebase"); break;
            case PullMode.FastForwardOnly: args.Add("--ff-only"); break;
        }
        if (autostash) args.Add("--autostash");
        if (remote is not null)
        {
            args.Add(remote);
            if (branch is not null) args.Add(branch);
        }
        return ExecuteAsync(args, cancellationToken, progress: progress, timeout: NetworkTimeout);
    }

    /// <param name="forceWithLeaseExpected">null: normal push; aksi hâlde uzaktaki beklenen sha ile --force-with-lease.</param>
    public Task<GitCommandResult> PushAsync(string remote, string localRef, string remoteBranch, bool setUpstream = false,
        string? forceWithLeaseExpected = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "push", "--progress" };
        if (setUpstream) args.Add("--set-upstream");
        if (forceWithLeaseExpected is not null)
            args.Add($"--force-with-lease=refs/heads/{remoteBranch}:{forceWithLeaseExpected}");
        args.Add(remote);
        args.Add($"{localRef}:refs/heads/{remoteBranch}");
        return ExecuteAsync(args, cancellationToken, progress: progress, timeout: NetworkTimeout);
    }

    public Task<GitCommandResult> DeleteRemoteBranchAsync(string remote, string branch, CancellationToken cancellationToken = default) =>
        ExecuteAsync(["push", remote, "--delete", branch], cancellationToken, timeout: NetworkTimeout);

    public Task<GitCommandResult> PushTagAsync(string remote, string tag, CancellationToken cancellationToken = default) =>
        ExecuteAsync(["push", remote, RefNames.Tag(tag)], cancellationToken, timeout: NetworkTimeout);
}

public sealed class TagService(IGitRunner runner, RepositoryInfo repository) : GitServiceBase(runner, repository)
{
    public Task<GitCommandResult> CreateAsync(string name, string revision, string? message = null, CancellationToken cancellationToken = default) =>
        message is null
            ? ExecuteAsync(["tag", name, revision], cancellationToken)
            : ExecuteAsync(["tag", "-a", name, "-F", "-", revision], cancellationToken, standardInput: Encoding.UTF8.GetBytes(message));

    public Task<GitCommandResult> DeleteAsync(string name, CancellationToken cancellationToken = default) =>
        ExecuteAsync(["tag", "-d", RefNames.Shorten(name)], cancellationToken);
}

public sealed class BlameService(IGitRunner runner, RepositoryInfo repository) : GitServiceBase(runner, repository)
{
    public async Task<IReadOnlyList<BlameLine>> BlameAsync(string path, string? revision = null, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "blame", "--line-porcelain", "-M" };
        if (revision is not null) args.Add(revision);
        args.Add("--");
        args.Add(path);
        var result = await ExecuteAsync(args, cancellationToken, readOnly: true).ConfigureAwait(false);
        return BlameParser.Parse(result.EnsureSuccess().StandardOutput);
    }
}

public sealed class WorktreeService(IGitRunner runner, RepositoryInfo repository) : GitServiceBase(runner, repository)
{
    public async Task<IReadOnlyList<WorktreeInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var output = await ReadAsync(cancellationToken, "worktree", "list", "--porcelain", "-z").ConfigureAwait(false);
        return WorktreeParser.Parse(output);
    }

    public Task<GitCommandResult> AddAsync(string path, string? existingBranch, string? newBranch, string? startPoint, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "worktree", "add" };
        if (newBranch is not null)
        {
            args.Add("-b");
            args.Add(newBranch);
        }
        args.Add(path);
        if (existingBranch is not null) args.Add(existingBranch);
        else if (startPoint is not null) args.Add(startPoint);
        return ExecuteAsync(args, cancellationToken);
    }

    public Task<GitCommandResult> RemoveAsync(string path, bool force, CancellationToken cancellationToken = default) =>
        ExecuteAsync(force ? ["worktree", "remove", "--force", path] : ["worktree", "remove", path], cancellationToken);
}

public sealed record BisectStep(string? CurrentSha, int? RemainingRevisions, int? RemainingSteps, string? FirstBadSha, string RawOutput)
{
    public bool IsFinished => FirstBadSha is not null;
}

public sealed class BisectService(IGitRunner runner, RepositoryInfo repository) : GitServiceBase(runner, repository)
{
    public async Task<BisectStep> StartAsync(string badRevision, string goodRevision, CancellationToken cancellationToken = default) =>
        ParseStep((await ExecuteAsync(["bisect", "start", badRevision, goodRevision, "--"], cancellationToken).ConfigureAwait(false)).EnsureSuccess());

    public async Task<BisectStep> MarkAsync(string term, CancellationToken cancellationToken = default) =>
        ParseStep((await ExecuteAsync(["bisect", term], cancellationToken).ConfigureAwait(false)).EnsureSuccess());

    public Task<GitCommandResult> ResetAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(["bisect", "reset"], cancellationToken);

    internal static BisectStep ParseStep(GitCommandResult result)
    {
        var output = result.StandardOutput;
        var firstBad = System.Text.RegularExpressions.Regex.Match(output, @"^([0-9a-f]{40,64}) is the first bad commit", System.Text.RegularExpressions.RegexOptions.Multiline);
        if (firstBad.Success) return new BisectStep(null, 0, 0, firstBad.Groups[1].Value, output);

        var progress = System.Text.RegularExpressions.Regex.Match(output, @"Bisecting: (\d+) revisions? left to test after this \(roughly (\d+) steps?\)\s*\[([0-9a-f]+)\]");
        return progress.Success
            ? new BisectStep(progress.Groups[3].Value, int.Parse(progress.Groups[1].Value), int.Parse(progress.Groups[2].Value), null, output)
            : new BisectStep(null, null, null, null, output);
    }
}

public sealed class ConfigService(IGitRunner runner, RepositoryInfo repository) : GitServiceBase(runner, repository)
{
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(["config", "--get", key], cancellationToken, readOnly: true, isInternal: true).ConfigureAwait(false);
        return result.Success ? result.StandardOutput.Trim() : null;
    }

    public Task<GitCommandResult> SetAsync(string key, string value, bool global, CancellationToken cancellationToken = default) =>
        ExecuteAsync(global ? ["config", "--global", key, value] : ["config", key, value], cancellationToken);
}
