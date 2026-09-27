using System.Text.Json;
using ArChrono.Git.Models;
using ArChrono.Localization;
using ArChrono.Recovery.Operations;
using ArChrono.Recovery.Points;
using ArChrono.Storage.Records;
using ArChrono.Storage.Stores;

namespace ArChrono.Recovery.Restore;

/// <summary>before / after / current durumlarını karşılaştırarak açık adımlardan oluşan plan üretir (recovery-model.md §4).</summary>
public sealed class RestorePlanner(ArChronoStorage storage, RecoveryPointService points)
{
    /// <summary>Bir işlemi geri almak için plan: yalnızca işlemin değiştirdiği şeyler geri alınır.</summary>
    public async Task<RestorePlan> PlanUndoAsync(OperationContext context, GitOperationRecord operation, CancellationToken cancellationToken = default)
    {
        if (operation.BeforePointId is not { } beforeId || storage.RecoveryPoints.Get(beforeId) is not { } before)
            return new RestorePlan(OperationTitles.Undo(operation.Title), 0, operation.StartedAt, [], [Loc.T("This operation has no recovery point, so it can't be undone automatically.", "Bu işlemin kurtarma noktası olmadığı için otomatik olarak geri alınamaz.")]);

        var after = operation.AfterPointId is { } afterId ? storage.RecoveryPoints.Get(afterId) : null;
        var current = await points.ReadCurrentAsync(context.Repository, RefScope.All, cancellationToken).ConfigureAwait(false);
        var beforeState = points.Load(before);
        var afterState = after is null ? null : points.Load(after);

        var title = operation.Kind == "undo" ? OperationTitles.Redo(operation.Title) : OperationTitles.Undo(operation.Title);

        var actions = new List<RestoreAction>();
        var notes = new List<string>();

        if (current.State.IsBlocking()) actions.Add(new AbortInProgressAction(current.State));

        // Ref değişiklikleri: after yoksa (after noktası alınamadıysa) current'a göre.
        var reference = afterState?.Refs ?? current.Refs;
        var renamed = TryReadRename(operation);
        foreach (var name in beforeState.Refs.Keys.Union(reference.Keys).Where(IsLocalRef).Order(StringComparer.Ordinal))
        {
            beforeState.Refs.TryGetValue(name, out var beforeSha);
            reference.TryGetValue(name, out var afterSha);
            if (beforeSha == afterSha) continue;
            if (renamed is not null && (name == RefNames.Branch(renamed.Value.OldName) || name == RefNames.Branch(renamed.Value.NewName))) continue;

            current.Refs.TryGetValue(name, out var currentSha);
            if (currentSha == beforeSha) continue;
            var warning = afterState is not null && currentSha != afterSha
                ? Loc.T($"{RefNames.Shorten(name)} changed again after the operation. Those changes stay in the recovery point created before this undo.", $"{RefNames.Shorten(name)} işlemden sonra yeniden değişti. Bu değişiklikler, geri almadan önce oluşturulan kurtarma noktasında kalır.")
                : null;
            actions.Add(new RefChangeAction(name, currentSha, beforeSha) { Warning = warning });
        }

        if (renamed is { } rename && current.Refs.ContainsKey(RefNames.Branch(rename.NewName)) && !current.Refs.ContainsKey(RefNames.Branch(rename.OldName)))
            actions.Add(new RenameBranchAction(rename.NewName, rename.OldName));

        if (HeadDiffers(beforeState.Head, current.Head))
            actions.Add(new SetHeadAction(beforeState.Head.BranchRef, beforeState.Head.BranchRef is null ? beforeState.Head.Sha : null));

        AddWorkingTreeAction(before, afterState, actions);
        AddStashActions(beforeState, afterState ?? current, current, actions, dropNewStashes: true);
        AddRemoteRestore(operation, beforeState, afterState, actions, notes);

        if (actions.Count == 0)
            notes.Add(operation.Kind == "push"
                ? Loc.T("A push only changed the remote. To take the change back, revert the commit and push again.", "Push yalnızca uzak depoyu değiştirdi. Değişikliği geri almak için commit'i revert edip tekrar push edin.")
                : Loc.T("Everything is already as it was before this operation.", "Her şey zaten bu işlemden önceki hâlinde."));

        return new RestorePlan(title, before.Id, before.CreatedAt, actions, notes);
    }

    /// <summary>Recovery Center'dan keyfi bir noktaya dönüş planı.</summary>
    public async Task<RestorePlan> PlanRestoreAsync(OperationContext context, RecoveryPointRecord point, RestoreMode mode, CancellationToken cancellationToken = default)
    {
        var current = await points.ReadCurrentAsync(context.Repository, RefScope.All, cancellationToken).ConfigureAwait(false);
        var target = points.Load(point);
        var actions = new List<RestoreAction>();
        var notes = new List<string>();

        if (current.State.IsBlocking() && mode != RestoreMode.BranchesOnly) actions.Add(new AbortInProgressAction(current.State));

        if (mode is RestoreMode.Everything or RestoreMode.BranchesOnly)
        {
            foreach (var (name, sha) in target.Refs.Where(r => IsLocalRef(r.Key)).OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                current.Refs.TryGetValue(name, out var currentSha);
                if (currentSha == sha) continue;
                // Silinmiş ref'ler geri gelir; mevcut başka branch'ler yalnızca HEAD'in branch'i ise varsayılan seçili.
                var isHeadBranch = name == target.Head.BranchRef;
                actions.Add(new RefChangeAction(name, currentSha, sha)
                {
                    Selected = currentSha is null || isHeadBranch,
                    Warning = currentSha is not null && !isHeadBranch ? Loc.T($"{RefNames.Shorten(name)} has moved since then. Select to move it back too.", $"{RefNames.Shorten(name)} o zamandan beri taşındı. Onu da geri taşımak için seçin.") : null,
                });
            }

            var laterRefs = current.Refs.Keys.Where(IsLocalRef).Where(n => !target.Refs.ContainsKey(n)).ToList();
            if (laterRefs.Count > 0)
                notes.Add(Loc.T($"{laterRefs.Count} branch(es) or tag(s) created later are kept: {string.Join(", ", laterRefs.Take(5).Select(RefNames.Shorten))}{(laterRefs.Count > 5 ? "…" : "")}", $"Sonradan oluşturulan {laterRefs.Count} branch veya tag korunur: {string.Join(", ", laterRefs.Take(5).Select(RefNames.Shorten))}{(laterRefs.Count > 5 ? "…" : "")}"));

            if (HeadDiffers(target.Head, current.Head))
                actions.Add(new SetHeadAction(target.Head.BranchRef, target.Head.BranchRef is null ? target.Head.Sha : null));
        }

        if (mode is RestoreMode.Everything or RestoreMode.FilesOnly)
        {
            AddWorkingTreeAction(point, null, actions);
            if (mode == RestoreMode.FilesOnly && HeadDiffers(target.Head, current.Head))
                notes.Add(Loc.T("You are on a different commit now. Restored files will show up as changes compared to it.", "Şu anda farklı bir commit üzerindesiniz. Geri yüklenen dosyalar ona göre değişiklik olarak görünecek."));
        }

        if (mode == RestoreMode.Everything)
            AddStashActions(target, null, current, actions, dropNewStashes: false);

        return new RestorePlan(Loc.T($"Restore to {point.CreatedAt:HH:mm:ss} — {point.Title}", $"{point.CreatedAt:HH:mm:ss} anına geri dön — {point.Title}"), point.Id, point.CreatedAt, actions, notes);
    }

    private void AddWorkingTreeAction(RecoveryPointRecord point, RepositoryStateSnapshot? afterState, List<RestoreAction> actions)
    {
        if (point.SnapshotId is not { } snapshotId || storage.Snapshots.Get(snapshotId) is not { } snapshot) return;
        if (afterState?.SnapshotId is { } afterSnapshotId && storage.Snapshots.Get(afterSnapshotId) is { } afterSnapshot
            && afterSnapshot.Fingerprint == snapshot.Fingerprint)
            return;
        actions.Add(new RestoreWorkingTreeAction(snapshotId, snapshot.CreatedAt, snapshot.EntryCount));
    }

    private static void AddStashActions(RepositoryStateSnapshot target, RepositoryStateSnapshot? after, RepositoryStateSnapshot current, List<RestoreAction> actions, bool dropNewStashes)
    {
        var currentShas = current.Stashes.Select(s => s.CommitSha).ToHashSet(StringComparer.Ordinal);
        var targetShas = target.Stashes.Select(s => s.CommitSha).ToHashSet(StringComparer.Ordinal);

        // En alttaki (en eski) önce geri eklenir; "stash store" en üste eklediği için sıra korunur.
        foreach (var stash in target.Stashes.OrderByDescending(s => s.Position).Where(s => !currentShas.Contains(s.CommitSha)))
            actions.Add(new StoreStashAction(stash.CommitSha, stash.Message));

        if (!dropNewStashes || after is null) return;
        var afterShas = after.Stashes.Select(s => s.CommitSha).ToHashSet(StringComparer.Ordinal);
        foreach (var stash in current.Stashes.Where(s => !targetShas.Contains(s.CommitSha) && afterShas.Contains(s.CommitSha)))
            actions.Add(new DropStashAction(stash.CommitSha, stash.Message));
    }

    private static void AddRemoteRestore(GitOperationRecord operation, RepositoryStateSnapshot before, RepositoryStateSnapshot? after, List<RestoreAction> actions, List<string> notes)
    {
        if (operation.Kind != "push" || after is null || operation.MetadataJson is null) return;
        using var json = JsonDocument.Parse(operation.MetadataJson);
        var root = json.RootElement;
        if (!root.TryGetProperty("force", out var force) || !force.GetBoolean()) return;
        var remote = root.GetProperty("remote").GetString()!;
        var branch = root.GetProperty("branch").GetString()!;
        var remoteRef = $"refs/remotes/{remote}/{branch}";
        if (!before.Refs.TryGetValue(remoteRef, out var oldSha) || !after.Refs.TryGetValue(remoteRef, out var newSha) || oldSha == newSha) return;

        actions.Add(new RestoreRemoteBranchAction(remote, branch, oldSha, newSha) { Selected = false });
        notes.Add(Loc.T("Force push changed the server. Restoring it is a separate step that affects everyone using this branch.", "Force push sunucuyu değiştirdi. Onu geri almak, bu branch'i kullanan herkesi etkileyen ayrı bir adımdır."));
    }

    private static (string OldName, string NewName)? TryReadRename(GitOperationRecord operation)
    {
        if (operation.Kind != "branch-rename" || operation.MetadataJson is null) return null;
        using var json = JsonDocument.Parse(operation.MetadataJson);
        return (json.RootElement.GetProperty("oldName").GetString()!, json.RootElement.GetProperty("newName").GetString()!);
    }

    private static bool IsLocalRef(string name) =>
        name.StartsWith(RefNames.HeadsPrefix, StringComparison.Ordinal) || name.StartsWith(RefNames.TagsPrefix, StringComparison.Ordinal);

    private static bool HeadDiffers(HeadInfo target, HeadInfo current) =>
        target.BranchRef is not null ? target.BranchRef != current.BranchRef : target.Sha is not null && (current.BranchRef is not null || current.Sha != target.Sha);
}
