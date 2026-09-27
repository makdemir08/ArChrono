using ArChrono.Git.Errors;
using ArChrono.Git.Services;
using ArChrono.Localization;
using ArChrono.Recovery.Operations;
using ArChrono.Recovery.Snapshots;
using ArChrono.Storage.Records;
using ArChrono.Storage.Stores;

namespace ArChrono.Recovery.Restore;

/// <summary>Planın seçili adımlarını sırayla uygular (recovery-model.md §5).</summary>
public sealed class RestoreExecutor(ArChronoStorage storage, WorkingTreeRestorer restorer)
{
    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, RestorePlan plan, CancellationToken cancellationToken = default)
    {
        var repository = context.Repository;
        var selected = plan.SelectedActions.ToList();
        var problems = new List<string>();

        foreach (var abort in selected.OfType<AbortInProgressAction>())
        {
            var result = await repository.Integration.AbortAsync(abort.State, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                // Abort edilemeyen durumda sequencer'ı bırak; dosyalar zaten snapshot'tan geri gelecek.
                var quit = abort.State switch
                {
                    Git.Models.RepositoryState.Rebasing => new[] { "rebase", "--quit" },
                    Git.Models.RepositoryState.CherryPicking => ["cherry-pick", "--quit"],
                    Git.Models.RepositoryState.Reverting => ["revert", "--quit"],
                    Git.Models.RepositoryState.Merging => ["merge", "--quit"],
                    _ => null,
                };
                if (quit is not null)
                    await repository.Runner.RunAsync(new Git.Process.GitCommand(repository.Info.RootPath, quit), cancellationToken).ConfigureAwait(false);
            }
        }

        var refUpdates = selected.OfType<RefChangeAction>()
            .Select(a => new RefUpdate(a.RefName, a.TargetSha, a.CurrentSha ?? string.Empty))
            .ToList();
        if (refUpdates.Count > 0)
        {
            try
            {
                await repository.Refs.UpdateRefsAsync(refUpdates, "archrono: " + plan.Title, cancellationToken).ConfigureAwait(false);
            }
            catch (GitException ex)
            {
                return ExecutionResult.Failure(ex.Error with
                {
                    Title = Loc.T("Branches could not be restored.", "Branch'ler geri yüklenemedi."),
                    Explanation = Loc.T("A branch changed while the plan was being applied. Nothing was changed; review the plan and try again.", "Plan uygulanırken bir branch değişti. Hiçbir şey değiştirilmedi; planı gözden geçirip tekrar deneyin."),
                });
            }
        }

        foreach (var rename in selected.OfType<RenameBranchAction>())
        {
            var result = await repository.Branches.RenameAsync(rename.CurrentName, rename.RestoredName, cancellationToken).ConfigureAwait(false);
            if (!result.Success) problems.Add(GitErrorTranslator.Translate(result).Title);
        }

        foreach (var head in selected.OfType<SetHeadAction>())
        {
            if (head.BranchRef is not null) await repository.Refs.SetHeadToBranchAsync(head.BranchRef, "archrono: " + plan.Title, cancellationToken).ConfigureAwait(false);
            else if (head.Sha is not null) await repository.Refs.DetachHeadAsync(head.Sha, "archrono: " + plan.Title, cancellationToken).ConfigureAwait(false);
        }

        foreach (var action in selected.OfType<RestoreWorkingTreeAction>())
        {
            var snapshot = storage.Snapshots.Get(action.SnapshotId);
            if (snapshot is null)
            {
                problems.Add(Loc.T("The file snapshot was removed by the storage limit, so files could not be restored.", "Dosya anlık görüntüsü depolama sınırı nedeniyle silindiği için dosyalar geri yüklenemedi."));
                continue;
            }
            var report = await restorer.RestoreAsync(repository, snapshot,
                new RestoreOptions { RemoveFilesCreatedAfterSnapshot = action.RemoveFilesCreatedAfterSnapshot }, cancellationToken).ConfigureAwait(false);
            problems.AddRange(report.Failures);
        }

        foreach (var store in selected.OfType<StoreStashAction>())
        {
            var result = await repository.Stash.StoreAsync(store.Sha, store.Message, cancellationToken).ConfigureAwait(false);
            if (!result.Success) problems.Add(Loc.T($"Stash \"{store.Message}\" could not be restored.", $"\"{store.Message}\" stash'i geri getirilemedi."));
        }

        foreach (var drop in selected.OfType<DropStashAction>())
        {
            var index = await StashApplyOperation.FindIndexAsync(context, drop.Sha, cancellationToken).ConfigureAwait(false);
            if (index is null) continue;
            var result = await repository.Stash.DropAsync(index.Value, cancellationToken).ConfigureAwait(false);
            if (!result.Success) problems.Add(Loc.T($"Stash \"{drop.Message}\" could not be removed.", $"\"{drop.Message}\" stash'i kaldırılamadı."));
        }

        foreach (var remote in selected.OfType<RestoreRemoteBranchAction>())
        {
            var result = await repository.Remotes.PushAsync(remote.Remote, remote.TargetSha, remote.Branch, forceWithLeaseExpected: remote.ExpectedRemoteSha,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.Success) return ExecutionResult.FromCommand(result);
        }

        if (problems.Count > 0)
        {
            return ExecutionResult.Failure(GitError.Simple(GitErrorCode.Unknown, Loc.T("Restore finished with problems.", "Geri yükleme sorunlarla tamamlandı."),
                string.Join("\n", problems.Take(10)), Loc.T("Check the Recovery Center; the state before this restore was saved.", "Kurtarma Merkezi'ni kontrol edin; bu geri yüklemeden önceki durum kaydedildi.")));
        }
        return ExecutionResult.Success(message: plan.Title);
    }
}

/// <summary>Undo, redo ve Recovery Center geri dönüşleri de Safe Git hattından geçer: önce "before restore" noktası alınır.</summary>
public sealed class RestoreOperation(RestorePlan plan, RestoreExecutor executor, string kind, long? undoOfOperationId) : IGitOperation
{
    public string Kind => kind;
    public string Title => plan.Title;
    public string CommandPreview => string.Join("\n", plan.SelectedActions.Select(a => a.CommandPreview));
    public OperationRisk Risk => plan.HasRemoteActions ? OperationRisk.Remote : OperationRisk.Reversible;
    public Points.RefScope RefScope => Points.RefScope.All;
    public long? UndoOfOperationId => undoOfOperationId;

    public Task<ValidationResult> ValidateAsync(OperationContext context, CancellationToken cancellationToken) =>
        Task.FromResult(plan.IsEmpty ? ValidationResult.Blocked(plan.Notes.FirstOrDefault() ?? Loc.T("There is nothing to restore.", "Geri yüklenecek bir şey yok.")) : ValidationResult.Ok());

    public Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken) =>
        executor.ExecuteAsync(context, plan, cancellationToken);

    public async Task<string?> VerifyAsync(OperationContext context, ExecutionResult result, CancellationToken cancellationToken)
    {
        var refs = await context.Repository.Refs.GetRefTargetsAsync(cancellationToken, "refs/heads", "refs/tags").ConfigureAwait(false);
        foreach (var change in plan.SelectedActions.OfType<RefChangeAction>())
        {
            refs.TryGetValue(change.RefName, out var actual);
            if (actual != change.TargetSha) return Loc.T($"{Git.Models.RefNames.Shorten(change.RefName)} is not where it should be after restoring.", $"{Git.Models.RefNames.Shorten(change.RefName)} geri yüklemeden sonra olması gereken yerde değil.");
        }
        return null;
    }
}
