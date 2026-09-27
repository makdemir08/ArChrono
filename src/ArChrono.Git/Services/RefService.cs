using System.Text;
using ArChrono.Git.Models;
using ArChrono.Git.Parsing;
using ArChrono.Git.Process;

namespace ArChrono.Git.Services;

/// <summary>Ref güncellemesi. <see cref="NewSha"/> null ise silme; <see cref="ExpectedOldSha"/> "" ise ref'in olmaması beklenir.</summary>
public sealed record RefUpdate(string RefName, string? NewSha, string? ExpectedOldSha = null);

public sealed class RefService(IGitRunner runner, RepositoryInfo repository) : GitServiceBase(runner, repository)
{
    public async Task<IReadOnlyList<RefInfo>> GetRefsAsync(CancellationToken cancellationToken = default)
    {
        var output = await ReadAsync(cancellationToken, "for-each-ref", "--format=" + RefParser.RefFormat, "refs/heads", "refs/remotes", "refs/tags").ConfigureAwait(false);
        return RefParser.ParseRefs(output);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetRefTargetsAsync(CancellationToken cancellationToken, params string[] patterns)
    {
        var args = new List<string> { "for-each-ref", "--format=%(refname)%1f%(objectname)" };
        args.AddRange(patterns);
        var result = await ExecuteAsync(args, cancellationToken, readOnly: true, isInternal: true).ConfigureAwait(false);
        return RefParser.ParseRefTargets(result.EnsureSuccess().StandardOutput);
    }

    public async Task<HeadInfo> GetHeadAsync(CancellationToken cancellationToken = default)
    {
        var symbolicTask = ExecuteAsync(["symbolic-ref", "-q", "HEAD"], cancellationToken, readOnly: true, isInternal: true);
        var shaTask = ExecuteAsync(["rev-parse", "-q", "--verify", "HEAD"], cancellationToken, readOnly: true, isInternal: true);
        await Task.WhenAll(symbolicTask, shaTask).ConfigureAwait(false);

        var symbolic = await symbolicTask;
        var sha = await shaTask;
        return new HeadInfo(
            symbolic.Success ? symbolic.StandardOutput.Trim() : null,
            sha.Success ? sha.StandardOutput.Trim() : null);
    }

    public async Task<string?> ResolveCommitAsync(string revision, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(["rev-parse", "-q", "--verify", "--end-of-options", revision + "^{commit}"], cancellationToken, readOnly: true, isInternal: true).ConfigureAwait(false);
        return result.Success ? result.StandardOutput.Trim() : null;
    }

    public async Task<string?> ResolveObjectAsync(string revision, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(["rev-parse", "-q", "--verify", "--end-of-options", revision], cancellationToken, readOnly: true, isInternal: true).ConfigureAwait(false);
        return result.Success ? result.StandardOutput.Trim() : null;
    }

    /// <summary>Tüm güncellemeleri tek bir atomik <c>update-ref --stdin</c> transaction'ında uygular.</summary>
    public async Task UpdateRefsAsync(IReadOnlyCollection<RefUpdate> updates, string reflogMessage, CancellationToken cancellationToken = default, bool isInternal = false)
    {
        if (updates.Count == 0) return;
        var script = new StringBuilder();
        foreach (var update in updates)
        {
            if (update.NewSha is null)
                script.Append("delete ").Append(update.RefName).Append(update.ExpectedOldSha is { Length: > 0 } old ? " " + old : "").Append('\n');
            else if (update.ExpectedOldSha == string.Empty)
                script.Append("create ").Append(update.RefName).Append(' ').Append(update.NewSha).Append('\n');
            else
                script.Append("update ").Append(update.RefName).Append(' ').Append(update.NewSha).Append(update.ExpectedOldSha is { } expected ? " " + expected : "").Append('\n');
        }

        var result = await ExecuteAsync(["update-ref", "-m", reflogMessage, "--stdin"], cancellationToken,
            standardInput: Encoding.UTF8.GetBytes(script.ToString()), isInternal: isInternal).ConfigureAwait(false);
        result.EnsureSuccess();
    }

    public Task SetHeadToBranchAsync(string branchRef, string reflogMessage, CancellationToken cancellationToken = default) =>
        WriteAsync(cancellationToken, "symbolic-ref", "-m", reflogMessage, "HEAD", RefNames.Branch(branchRef));

    public Task DetachHeadAsync(string sha, string reflogMessage, CancellationToken cancellationToken = default) =>
        WriteAsync(cancellationToken, "update-ref", "--no-deref", "-m", reflogMessage, "HEAD", sha);

    public async Task<bool> IsValidBranchNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var result = await ExecuteAsync(["check-ref-format", "--branch", name], cancellationToken, readOnly: true, isInternal: true).ConfigureAwait(false);
        return result.Success;
    }
}
