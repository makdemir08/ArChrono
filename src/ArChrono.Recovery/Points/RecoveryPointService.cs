using ArChrono.Git;
using ArChrono.Git.Models;
using ArChrono.Recovery.Snapshots;
using ArChrono.Storage.Records;
using ArChrono.Storage.Stores;

namespace ArChrono.Recovery.Points;

[Flags]
public enum RefScope
{
    None = 0,
    Branches = 1,
    Remotes = 2,
    Tags = 4,
    Default = Branches | Remotes,
    All = Branches | Remotes | Tags,
}

/// <summary>Bir andaki repository durumu: HEAD, ref hedefleri, stash listesi, yarım işlem.</summary>
public sealed record RepositoryStateSnapshot(
    HeadInfo Head,
    IReadOnlyDictionary<string, string> Refs,
    IReadOnlyList<RecoveryStashRecord> Stashes,
    RepositoryState State,
    long? SnapshotId);

public sealed class RecoveryPointService(ArChronoStorage storage, SnapshotEngine snapshots, PinManager pins)
{
    public async Task<RecoveryPointRecord> CaptureAsync(GitRepository repository, long repositoryId, RecoveryPointKind kind, string title,
        long? operationId = null, RefScope scope = RefScope.Default, bool includeSnapshot = true, string? metadataJson = null,
        CancellationToken cancellationToken = default)
    {
        var state = await ReadCurrentAsync(repository, scope, cancellationToken).ConfigureAwait(false);

        SnapshotRecord? snapshot = null;
        if (includeSnapshot)
        {
            var result = await snapshots.CaptureAsync(repository, repositoryId, SnapshotTrigger.Operation, title, cancellationToken: cancellationToken).ConfigureAwait(false);
            snapshot = result.Snapshot;
        }

        var point = new RecoveryPointRecord(0, repositoryId, DateTimeOffset.Now, kind, title, operationId,
            state.Head.BranchRef, state.Head.Sha, state.Head.BranchName, state.State.ToStorageValue(),
            snapshot?.Id, snapshot?.EntryCount ?? 0, IsPinned: false, metadataJson);

        var refs = state.Refs.Select(r => new RecoveryRefRecord(r.Key, r.Value)).ToList();
        return storage.RecoveryPoints.Insert(point, refs, state.Stashes);
    }

    public async Task<RepositoryStateSnapshot> ReadCurrentAsync(GitRepository repository, RefScope scope = RefScope.All, CancellationToken cancellationToken = default)
    {
        var patterns = new List<string>();
        if (scope.HasFlag(RefScope.Branches)) patterns.Add("refs/heads");
        if (scope.HasFlag(RefScope.Remotes)) patterns.Add("refs/remotes");
        if (scope.HasFlag(RefScope.Tags)) patterns.Add("refs/tags");

        var headTask = repository.Refs.GetHeadAsync(cancellationToken);
        var refsTask = patterns.Count == 0
            ? Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>())
            : repository.Refs.GetRefTargetsAsync(cancellationToken, patterns.ToArray());
        var stashTask = repository.Stash.ListAsync(cancellationToken, isInternal: true);
        await Task.WhenAll(headTask, refsTask, stashTask).ConfigureAwait(false);

        var stashes = (await stashTask).Select(s => new RecoveryStashRecord(s.Index, s.Sha, s.Message)).ToList();
        return new RepositoryStateSnapshot(await headTask, await refsTask, stashes, repository.Status.GetState(), null);
    }

    public RepositoryStateSnapshot Load(RecoveryPointRecord point) =>
        new(new HeadInfo(point.HeadRef, point.HeadSha),
            storage.RecoveryPoints.GetRefs(point.Id).ToDictionary(r => r.RefName, r => r.TargetSha, StringComparer.Ordinal),
            storage.RecoveryPoints.GetStashes(point.Id),
            RepositoryStateExtensions.FromStorageValue(point.RepoState),
            point.SnapshotId);

    /// <summary>
    /// İşlem sonrası artık hiçbir ref'in göstermediği commit'leri (ör. silinen branch, reset edilen commit, düşen stash)
    /// pin ref'leriyle gc'den korur.
    /// </summary>
    public async Task ProtectLostObjectsAsync(GitRepository repository, RecoveryPointRecord before, RecoveryPointRecord? after, CancellationToken cancellationToken = default)
    {
        if (!pins.IsEnabled) return;
        var beforeState = Load(before);
        var afterState = after is null ? await ReadCurrentAsync(repository, RefScope.All, cancellationToken).ConfigureAwait(false) : Load(after);

        var stillReferenced = afterState.Refs.Values.ToHashSet(StringComparer.Ordinal);
        foreach (var stash in afterState.Stashes) stillReferenced.Add(stash.CommitSha);
        if (afterState.Head.Sha is { } afterHead) stillReferenced.Add(afterHead);

        var candidates = beforeState.Refs.Values
            .Concat(beforeState.Stashes.Select(s => s.CommitSha))
            .Append(beforeState.Head.Sha)
            .OfType<string>()
            .Where(sha => !stillReferenced.Contains(sha))
            .Distinct()
            .ToList();

        if (candidates.Count > 0)
            await pins.EnsurePinnedAsync(repository, candidates, cancellationToken).ConfigureAwait(false);
    }
}
