using System.Text.Json;
using ArChrono.Git.Errors;
using ArChrono.Git.Models;
using ArChrono.Git.Process;
using ArChrono.Git.Services;
using ArChrono.Localization;
using ArChrono.Recovery.Points;
using ArChrono.Storage.Records;

namespace ArChrono.Recovery.Operations;

internal static class Short
{
    public static string Sha(string? sha) => sha is null ? Loc.T("(none)", "(yok)") : sha.Length > 7 ? sha[..7] : sha;

    public static string Quote(string text) => text.Contains(' ') ? $"\"{text}\"" : text;
}

internal static class Messages
{
    public static string FinishInProgress => Loc.T("Finish or abort the operation that is already in progress.", "Devam eden işlemi önce bitirin veya iptal edin.");
}

public sealed class CommitOperation(string message, bool amend) : IGitOperation
{
    public string Kind => amend ? "amend" : "commit";
    public string Title => amend ? Loc.T("Amend last commit", "Son commit'i değiştir (amend)") : $"Commit \"{FirstLine}\"";
    public string CommandPreview => amend ? "git commit --amend" : "git commit -m " + Short.Quote(FirstLine);
    public OperationRisk Risk => amend ? OperationRisk.Destructive : OperationRisk.Reversible;
    private string FirstLine => message.Split('\n')[0].Trim();

    public async Task<ValidationResult> ValidateAsync(OperationContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message)) return ValidationResult.Blocked(Loc.T("Write a message that describes the change.", "Değişikliği anlatan bir mesaj yazın."));
        var status = await context.Repository.Status.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.ConflictedCount > 0) return ValidationResult.Blocked(Loc.T("Resolve the conflicted files before committing.", "Commit etmeden önce çakışmalı dosyaları çözün."));
        if (!amend && status.StagedCount == 0 && status.State != RepositoryState.Merging)
            return ValidationResult.Blocked(Loc.T("Nothing is staged. Choose the files to include first.", "Stage edilmiş bir şey yok. Önce eklenecek dosyaları seçin."));
        if (amend && status.Branch.IsUnborn) return ValidationResult.Blocked(Loc.T("There is no commit to amend yet.", "Henüz değiştirilecek bir commit yok."));
        return ValidationResult.Ok();
    }

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken) =>
        ExecutionResult.FromCommand(await context.Repository.WorkingTree.CommitAsync(message, amend, cancellationToken: cancellationToken).ConfigureAwait(false));
}

public sealed class CreateBranchOperation(string name, string? startPoint, bool checkout) : IGitOperation
{
    public string Kind => "branch-create";
    public string Title => checkout ? Loc.T($"Create and switch to branch {name}", $"{name} branch'ini oluştur ve geç") : Loc.T($"Create branch {name}", $"{name} branch'ini oluştur");
    public string CommandPreview => checkout ? $"git switch -c {name} {startPoint}".TrimEnd() : $"git branch {name} {startPoint}".TrimEnd();
    public OperationRisk Risk => OperationRisk.Reversible;
    public bool CapturesWorkingTree => checkout;

    public async Task<ValidationResult> ValidateAsync(OperationContext context, CancellationToken cancellationToken)
    {
        if (!await context.Repository.Refs.IsValidBranchNameAsync(name, cancellationToken).ConfigureAwait(false))
            return ValidationResult.Blocked(Loc.T($"'{name}' is not a valid branch name. Avoid spaces and characters like ~ ^ : ? * [ \\.", $"'{name}' geçerli bir branch adı değil. Boşluk ve ~ ^ : ? * [ \\ gibi karakterler kullanmayın."));
        if (await context.Repository.Refs.ResolveObjectAsync(RefNames.Branch(name), cancellationToken).ConfigureAwait(false) is not null)
            return ValidationResult.Blocked(Loc.T($"A branch named '{name}' already exists.", $"'{name}' adında bir branch zaten var."));
        return ValidationResult.Ok();
    }

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken) =>
        ExecutionResult.FromCommand(await context.Repository.Branches.CreateAsync(name, startPoint, checkout, cancellationToken).ConfigureAwait(false));

    public async Task<string?> VerifyAsync(OperationContext context, ExecutionResult result, CancellationToken cancellationToken) =>
        await context.Repository.Refs.ResolveObjectAsync(RefNames.Branch(name), cancellationToken).ConfigureAwait(false) is null
            ? Loc.T("The branch was not created.", "Branch oluşturulamadı.") : null;
}

public sealed class DeleteBranchOperation(string name, bool force) : IGitOperation
{
    public string Kind => "branch-delete";
    public string Title => Loc.T($"Delete branch {RefNames.Shorten(name)}", $"{RefNames.Shorten(name)} branch'ini sil");
    public string CommandPreview => $"git branch {(force ? "-D" : "-d")} {RefNames.Shorten(name)}";
    public OperationRisk Risk => OperationRisk.Destructive;
    public bool CapturesWorkingTree => false;

    public async Task<ValidationResult> ValidateAsync(OperationContext context, CancellationToken cancellationToken)
    {
        var head = await context.Repository.Refs.GetHeadAsync(cancellationToken).ConfigureAwait(false);
        if (head.BranchRef == RefNames.Branch(RefNames.Shorten(name)))
            return ValidationResult.Blocked(Loc.T("You can't delete the branch you are currently on. Switch to another branch first.", "Üzerinde bulunduğunuz branch'i silemezsiniz. Önce başka bir branch'e geçin."));
        return ValidationResult.Ok();
    }

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken) =>
        ExecutionResult.FromCommand(await context.Repository.Branches.DeleteAsync(name, force, cancellationToken).ConfigureAwait(false));
}

public sealed class RenameBranchOperation(string oldName, string newName) : IGitOperation
{
    public string Kind => "branch-rename";
    public string Title => Loc.T($"Rename branch {RefNames.Shorten(oldName)} to {newName}", $"{RefNames.Shorten(oldName)} branch'inin adını {newName} yap");
    public string CommandPreview => $"git branch -m {RefNames.Shorten(oldName)} {newName}";
    public OperationRisk Risk => OperationRisk.Reversible;
    public bool CapturesWorkingTree => false;
    public string? MetadataJson => JsonSerializer.Serialize(new { oldName = RefNames.Shorten(oldName), newName });

    public async Task<ValidationResult> ValidateAsync(OperationContext context, CancellationToken cancellationToken) =>
        await context.Repository.Refs.IsValidBranchNameAsync(newName, cancellationToken).ConfigureAwait(false)
            ? ValidationResult.Ok()
            : ValidationResult.Blocked(Loc.T($"'{newName}' is not a valid branch name.", $"'{newName}' geçerli bir branch adı değil."));

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken) =>
        ExecutionResult.FromCommand(await context.Repository.Branches.RenameAsync(oldName, newName, cancellationToken).ConfigureAwait(false));
}

public sealed class CheckoutOperation : IGitOperation
{
    private readonly string _target;
    private readonly bool _detached;
    private readonly string? _newLocalBranch;

    private CheckoutOperation(string target, bool detached, string? newLocalBranch)
    {
        _target = target;
        _detached = detached;
        _newLocalBranch = newLocalBranch;
    }

    public static CheckoutOperation Branch(string branch) => new(RefNames.Shorten(branch), false, null);

    public static CheckoutOperation RemoteBranch(string remoteBranch, string localName) => new(RefNames.Shorten(remoteBranch), false, localName);

    public static CheckoutOperation Detached(string revision) => new(revision, true, null);

    public string Kind => "checkout";
    public string Title => _detached ? Loc.T($"Switch to commit {Short.Sha(_target)}", $"{Short.Sha(_target)} commit'ine geç") : Loc.T($"Switch to {_newLocalBranch ?? _target}", $"{_newLocalBranch ?? _target} branch'ine geç");
    public string CommandPreview => _detached ? $"git switch --detach {_target}"
        : _newLocalBranch is not null ? $"git switch -c {_newLocalBranch} --track {_target}" : $"git switch {_target}";
    public OperationRisk Risk => OperationRisk.Reversible;

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken)
    {
        var branches = context.Repository.Branches;
        var result = _detached ? await branches.SwitchDetachedAsync(_target, cancellationToken).ConfigureAwait(false)
            : _newLocalBranch is not null ? await branches.SwitchToRemoteAsync(_target, _newLocalBranch, cancellationToken).ConfigureAwait(false)
            : await branches.SwitchAsync(_target, cancellationToken).ConfigureAwait(false);
        return ExecutionResult.FromCommand(result);
    }
}

public sealed class MergeOperation(string revision, FastForwardMode fastForward = FastForwardMode.Auto, bool allowUnrelatedHistories = false) : IGitOperation
{
    public string Kind => "merge";
    public string Title => $"Merge {RefNames.Shorten(revision)}";
    public string CommandPreview => "git merge" + fastForward switch { FastForwardMode.Only => " --ff-only", FastForwardMode.Never => " --no-ff", _ => "" } + " " + RefNames.Shorten(revision);
    public OperationRisk Risk => OperationRisk.Reversible;

    public Task<ValidationResult> ValidateAsync(OperationContext context, CancellationToken cancellationToken) =>
        Task.FromResult(context.Repository.Status.GetState().IsBlocking()
            ? ValidationResult.Blocked(Messages.FinishInProgress)
            : ValidationResult.Ok());

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken) =>
        ExecutionResult.FromCommand(await context.Repository.Integration.MergeAsync(revision, fastForward, allowUnrelatedHistories: allowUnrelatedHistories,
            cancellationToken: cancellationToken).ConfigureAwait(false), conflictsPossible: true);
}

public sealed class RebaseOperation(string upstream, bool autostash) : IGitOperation
{
    public string Kind => "rebase";
    public string Title => Loc.T($"Rebase onto {RefNames.Shorten(upstream)}", $"{RefNames.Shorten(upstream)} üzerine rebase");
    public string CommandPreview => $"git rebase{(autostash ? " --autostash" : "")} {RefNames.Shorten(upstream)}";
    public OperationRisk Risk => OperationRisk.Destructive;

    public async Task<ValidationResult> ValidateAsync(OperationContext context, CancellationToken cancellationToken)
    {
        if (context.Repository.Status.GetState().IsBlocking()) return ValidationResult.Blocked(Messages.FinishInProgress);
        var head = await context.Repository.Refs.GetHeadAsync(cancellationToken).ConfigureAwait(false);
        if (head.IsDetached) return ValidationResult.Ok(Loc.T("You are not on a branch; the rebased commits will only be reachable from HEAD.", "Bir branch üzerinde değilsiniz; rebase edilen commit'lere yalnızca HEAD üzerinden ulaşılabilir."));
        return ValidationResult.Ok();
    }

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken) =>
        ExecutionResult.FromCommand(await context.Repository.Integration.RebaseAsync(upstream, autostash: autostash, cancellationToken: cancellationToken).ConfigureAwait(false), conflictsPossible: true);
}

public sealed class InteractiveRebaseOperation(string? baseRevision, IReadOnlyList<RebaseTodoItem> items, bool autostash) : IGitOperation
{
    public string Kind => "rebase-interactive";
    public string Title => Loc.T($"Edit history ({items.Count} commits)", $"Geçmişi düzenle ({items.Count} commit)");
    public string CommandPreview => $"git rebase -i {(baseRevision is null ? "--root" : Short.Sha(baseRevision))}";
    public OperationRisk Risk => OperationRisk.Destructive;

    public Task<ValidationResult> ValidateAsync(OperationContext context, CancellationToken cancellationToken) =>
        Task.FromResult(context.Repository.Status.GetState().IsBlocking()
            ? ValidationResult.Blocked(Messages.FinishInProgress)
            : items.Count == 0 ? ValidationResult.Blocked(Loc.T("There are no commits to edit.", "Düzenlenecek commit yok.")) : ValidationResult.Ok());

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken)
    {
        var result = await context.Repository.Integration.InteractiveRebaseAsync(baseRevision, items, autostash, cancellationToken).ConfigureAwait(false);
        if (!result.Success && context.Repository.Status.GetState() == RepositoryState.Rebasing && items.Any(i => i.Action == RebaseAction.Edit)
            && !result.StandardError.Contains("CONFLICT", StringComparison.Ordinal))
            return new ExecutionResult(OperationStatus.Conflicted, GitErrorTranslator.Translate(result), result);
        var execution = ExecutionResult.FromCommand(result, conflictsPossible: true);
        // "edit" adımında rebase başarıyla durur: kullanıcı değişikliği yapıp Continue der.
        if (execution.Succeeded && context.Repository.Status.GetState() == RepositoryState.Rebasing)
            return execution with { Status = OperationStatus.Conflicted, Message = Loc.T("Stopped for editing. Make your changes, then continue.", "Düzenleme için durdu. Değişikliklerinizi yapın, sonra devam edin.") };
        return execution;
    }
}

public sealed class CherryPickOperation(IReadOnlyList<CommitInfo> commits) : IGitOperation
{
    public string Kind => "cherry-pick";
    public string Title => commits.Count == 1 ? $"Cherry-pick \"{commits[0].Subject}\"" : Loc.T($"Cherry-pick {commits.Count} commits", $"Cherry-pick ({commits.Count} commit)");
    public string CommandPreview => "git cherry-pick " + string.Join(' ', commits.Select(c => c.ShortSha));
    public OperationRisk Risk => OperationRisk.Destructive;

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken) =>
        ExecutionResult.FromCommand(await context.Repository.Integration.CherryPickAsync(commits, cancellationToken: cancellationToken).ConfigureAwait(false), conflictsPossible: true);
}

public sealed class RevertOperation(CommitInfo commit) : IGitOperation
{
    public string Kind => "revert";
    public string Title => $"Revert \"{commit.Subject}\"";
    public string CommandPreview => $"git revert {commit.ShortSha}";
    public OperationRisk Risk => OperationRisk.Destructive;

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken) =>
        ExecutionResult.FromCommand(await context.Repository.Integration.RevertAsync(commit, cancellationToken).ConfigureAwait(false), conflictsPossible: true);
}

public sealed class ResetOperation(string revision, ResetMode mode, string? branchName) : IGitOperation
{
    private string? _resolvedTarget;

    public string Kind => "reset";
    public string Title => Loc.T($"Reset {branchName ?? "HEAD"} to {Short.Sha(revision)} ({mode.ToString().ToLowerInvariant()})", $"Reset {branchName ?? "HEAD"} → {Short.Sha(revision)} ({mode.ToString().ToLowerInvariant()})");
    public string CommandPreview => $"git reset --{mode.ToString().ToLowerInvariant()} {Short.Sha(revision)}";
    public OperationRisk Risk => mode == ResetMode.Hard ? OperationRisk.Destructive : OperationRisk.Reversible;

    public async Task<ValidationResult> ValidateAsync(OperationContext context, CancellationToken cancellationToken)
    {
        _resolvedTarget = await context.Repository.Refs.ResolveCommitAsync(revision, cancellationToken).ConfigureAwait(false);
        return _resolvedTarget is null ? ValidationResult.Blocked(Loc.T($"Commit '{revision}' could not be found.", $"'{revision}' commit'i bulunamadı.")) : ValidationResult.Ok();
    }

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken) =>
        ExecutionResult.FromCommand(await context.Repository.Integration.ResetAsync(_resolvedTarget ?? revision, mode, cancellationToken).ConfigureAwait(false));

    public async Task<string?> VerifyAsync(OperationContext context, ExecutionResult result, CancellationToken cancellationToken)
    {
        var head = await context.Repository.Refs.GetHeadAsync(cancellationToken).ConfigureAwait(false);
        return head.Sha == _resolvedTarget ? null : Loc.T("HEAD did not move to the expected commit.", "HEAD beklenen commit'e taşınmadı.");
    }
}

public sealed class StashPushOperation(string? message, bool includeUntracked) : IGitOperation
{
    public string Kind => "stash-push";
    public string Title => string.IsNullOrWhiteSpace(message) ? Loc.T("Put changes aside (stash)", "Değişiklikleri kenara koy (stash)") : $"Stash \"{message}\"";
    public string CommandPreview => $"git stash push{(includeUntracked ? " -u" : "")}{(string.IsNullOrWhiteSpace(message) ? "" : " -m " + Short.Quote(message))}";
    public OperationRisk Risk => OperationRisk.Reversible;

    public async Task<ValidationResult> ValidateAsync(OperationContext context, CancellationToken cancellationToken)
    {
        var status = await context.Repository.Status.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var hasChanges = status.Entries.Any(e => e.HasStagedChanges || (e.HasWorktreeChanges && (includeUntracked || !e.IsUntracked)));
        return hasChanges ? ValidationResult.Ok() : ValidationResult.Blocked(Loc.T("There are no changes to put aside.", "Kenara koyulacak değişiklik yok."));
    }

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken) =>
        ExecutionResult.FromCommand(await context.Repository.Stash.PushAsync(message, includeUntracked, cancellationToken: cancellationToken).ConfigureAwait(false));
}

public sealed class StashApplyOperation(StashEntry stash, bool pop) : IGitOperation
{
    public string Kind => pop ? "stash-pop" : "stash-apply";
    public string Title => (pop ? Loc.T("Restore and remove stash ", "Stash'i geri getir ve kaldır: ") : Loc.T("Apply stash ", "Stash'i uygula: ")) + $"\"{stash.Message}\"";
    public string CommandPreview => $"git stash {(pop ? "pop" : "apply")} {stash.Selector}";
    public OperationRisk Risk => OperationRisk.Reversible;

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken)
    {
        var index = await FindIndexAsync(context, stash.Sha, cancellationToken).ConfigureAwait(false);
        if (index is null) return ExecutionResult.Failure(GitError.Simple(GitErrorCode.StashNotFound, Loc.T("That stash no longer exists.", "Bu stash artık yok."), stash.Message));
        var result = pop
            ? await context.Repository.Stash.PopAsync(index.Value, cancellationToken: cancellationToken).ConfigureAwait(false)
            : await context.Repository.Stash.ApplyAsync(index.Value, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ExecutionResult.FromCommand(result, conflictsPossible: true);
    }

    internal static async Task<int?> FindIndexAsync(OperationContext context, string sha, CancellationToken cancellationToken) =>
        (await context.Repository.Stash.ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(s => s.Sha == sha)?.Index;
}

public sealed class StashDropOperation(StashEntry stash) : IGitOperation
{
    public string Kind => "stash-drop";
    public string Title => Loc.T($"Delete stash \"{stash.Message}\"", $"Stash'i sil: \"{stash.Message}\"");
    public string CommandPreview => $"git stash drop {stash.Selector}";
    public OperationRisk Risk => OperationRisk.Destructive;
    public bool CapturesWorkingTree => false;

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken)
    {
        var index = await StashApplyOperation.FindIndexAsync(context, stash.Sha, cancellationToken).ConfigureAwait(false);
        if (index is null) return ExecutionResult.Failure(GitError.Simple(GitErrorCode.StashNotFound, Loc.T("That stash no longer exists.", "Bu stash artık yok."), stash.Message));
        return ExecutionResult.FromCommand(await context.Repository.Stash.DropAsync(index.Value, cancellationToken).ConfigureAwait(false));
    }
}

/// <summary>Seçili dosyalardaki değişiklikleri atar: takipli dosyalar index/HEAD'e döner, untracked dosyalar silinir.</summary>
public sealed class DiscardChangesOperation(IReadOnlyCollection<string> trackedPaths, IReadOnlyCollection<string> untrackedPaths, bool includeStaged) : IGitOperation
{
    public string Kind => "discard";
    public string Title => (trackedPaths.Count + untrackedPaths.Count) == 1
        ? Loc.T($"Discard changes in {trackedPaths.Concat(untrackedPaths).First()}", $"{trackedPaths.Concat(untrackedPaths).First()} dosyasındaki değişiklikleri at")
        : Loc.T($"Discard changes in {trackedPaths.Count + untrackedPaths.Count} files", $"{trackedPaths.Count + untrackedPaths.Count} dosyadaki değişiklikleri at");
    public string CommandPreview => string.Join(" && ", new[]
    {
        trackedPaths.Count > 0 ? (includeStaged ? "git restore --source=HEAD --staged --worktree -- …" : "git restore --worktree -- …") : null,
        untrackedPaths.Count > 0 ? "git clean -f -- …" : null,
    }.OfType<string>());
    public OperationRisk Risk => OperationRisk.Destructive;

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken)
    {
        if (trackedPaths.Count > 0)
        {
            var head = await context.Repository.Refs.GetHeadAsync(cancellationToken).ConfigureAwait(false);
            var result = includeStaged && !head.IsUnborn
                ? await context.Repository.WorkingTree.RestoreFromHeadAsync(trackedPaths, cancellationToken).ConfigureAwait(false)
                : await context.Repository.WorkingTree.RestoreWorktreeAsync(trackedPaths, cancellationToken).ConfigureAwait(false);
            if (!result.Success) return ExecutionResult.FromCommand(result);
        }
        if (untrackedPaths.Count > 0) context.Repository.WorkingTree.DeleteUntrackedFiles(untrackedPaths);
        return ExecutionResult.Success();
    }
}

public sealed class TagCreateOperation(string name, string revision, string? message) : IGitOperation
{
    public string Kind => "tag-create";
    public string Title => Loc.T($"Create tag {name}", $"{name} tag'ini oluştur");
    public string CommandPreview => message is null ? $"git tag {name} {Short.Sha(revision)}" : $"git tag -a {name} {Short.Sha(revision)}";
    public OperationRisk Risk => OperationRisk.Reversible;
    public RefScope RefScope => RefScope.All;
    public bool CapturesWorkingTree => false;

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken) =>
        ExecutionResult.FromCommand(await context.Repository.Tags.CreateAsync(name, revision, message, cancellationToken).ConfigureAwait(false));
}

public sealed class TagDeleteOperation(string name) : IGitOperation
{
    public string Kind => "tag-delete";
    public string Title => Loc.T($"Delete tag {RefNames.Shorten(name)}", $"{RefNames.Shorten(name)} tag'ini sil");
    public string CommandPreview => $"git tag -d {RefNames.Shorten(name)}";
    public OperationRisk Risk => OperationRisk.Destructive;
    public RefScope RefScope => RefScope.All;
    public bool CapturesWorkingTree => false;

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken) =>
        ExecutionResult.FromCommand(await context.Repository.Tags.DeleteAsync(name, cancellationToken).ConfigureAwait(false));
}

public sealed class AbortOperation(RepositoryState state) : IGitOperation
{
    public string Kind => "abort";
    public string Title => Loc.T($"Abort {Describe(state)}", $"İptal: {Describe(state)}");
    public string CommandPreview => $"git {Command(state)} --abort";
    public OperationRisk Risk => OperationRisk.Destructive;

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken) =>
        ExecutionResult.FromCommand(await context.Repository.Integration.AbortAsync(state, cancellationToken).ConfigureAwait(false));

    /// <summary>Kullanıcıya gösterilen ad (Git terimleri her iki dilde de aynıdır).</summary>
    internal static string Describe(RepositoryState state) => state switch
    {
        RepositoryState.ApplyingPatches => Loc.T("patch series", "patch serisi"),
        RepositoryState.Merging or RepositoryState.Rebasing or RepositoryState.CherryPicking or RepositoryState.Reverting => Command(state),
        _ => Loc.T("operation", "işlem"),
    };

    internal static string Command(RepositoryState state) => state switch
    {
        RepositoryState.Merging => "merge",
        RepositoryState.Rebasing => "rebase",
        RepositoryState.CherryPicking => "cherry-pick",
        RepositoryState.Reverting => "revert",
        RepositoryState.ApplyingPatches => "am",
        _ => "operation",
    };
}

public sealed class ContinueOperation(RepositoryState state) : IGitOperation
{
    public string Kind => "continue";
    public string Title => Loc.T($"Continue {AbortOperation.Describe(state)}", $"Devam: {AbortOperation.Describe(state)}");
    public string CommandPreview => $"git {AbortOperation.Command(state)} --continue";
    public OperationRisk Risk => OperationRisk.Reversible;

    public async Task<ValidationResult> ValidateAsync(OperationContext context, CancellationToken cancellationToken)
    {
        var status = await context.Repository.Status.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return status.ConflictedCount > 0
            ? ValidationResult.Blocked(Loc.T($"{status.ConflictedCount} file(s) still have conflicts. Resolve them first.", $"{status.ConflictedCount} dosyada hâlâ çakışma var. Önce bunları çözün."))
            : ValidationResult.Ok();
    }

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken)
    {
        var result = await context.Repository.Integration.ContinueAsync(state, cancellationToken).ConfigureAwait(false);
        var execution = ExecutionResult.FromCommand(result, conflictsPossible: true);
        if (execution.Succeeded && context.Repository.Status.GetState().IsBlocking())
            return execution with { Status = OperationStatus.Conflicted };
        return execution;
    }
}

public sealed class ResolveConflictOperation(ConflictFile conflict, bool useOurs) : IGitOperation
{
    public string Kind => "resolve-conflict";
    public string Title => Loc.T($"Use {(useOurs ? "current" : "incoming")} version of {conflict.Path}", $"{conflict.Path}: {(useOurs ? "mevcut" : "gelen")} sürümü kullan");
    public string CommandPreview => $"git checkout --{(useOurs ? "ours" : "theirs")} -- {conflict.Path} && git add -- {conflict.Path}";
    public OperationRisk Risk => OperationRisk.Reversible;

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken) =>
        ExecutionResult.FromCommand(await context.Repository.Conflicts.ResolveWithSideAsync(conflict, useOurs, cancellationToken).ConfigureAwait(false));
}

public sealed class FetchOperation(string? remote, IProgress<string>? progress) : IGitOperation
{
    public string Kind => "fetch";
    public string Title => remote is null ? Loc.T("Fetch all remotes", "Tüm remote'ları fetch et") : $"Fetch {remote}";
    public string CommandPreview => $"git fetch --prune {remote ?? "--all"}";
    public OperationRisk Risk => OperationRisk.Safe;

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken) =>
        ExecutionResult.FromCommand(await context.Repository.Remotes.FetchAsync(remote, progress: progress, cancellationToken: cancellationToken).ConfigureAwait(false));
}

public sealed class PullOperation(PullMode mode, bool autostash, IProgress<string>? progress) : IGitOperation
{
    public string Kind => "pull";
    public string Title => mode == PullMode.Rebase ? "Pull (rebase)" : "Pull";
    public string CommandPreview => "git pull" + mode switch { PullMode.Rebase => " --rebase", PullMode.Merge => " --no-rebase", PullMode.FastForwardOnly => " --ff-only", _ => "" };
    public OperationRisk Risk => mode == PullMode.Rebase ? OperationRisk.Destructive : OperationRisk.Reversible;

    public async Task<ValidationResult> ValidateAsync(OperationContext context, CancellationToken cancellationToken)
    {
        var status = await context.Repository.Status.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.Branch.IsDetached) return ValidationResult.Blocked(Loc.T("You are not on a branch. Switch to a branch to pull.", "Bir branch üzerinde değilsiniz. Pull için bir branch'e geçin."));
        if (status.Branch.Upstream is null) return ValidationResult.Blocked(Loc.T("This branch is not connected to a remote branch yet. Push it first.", "Bu branch henüz bir uzak branch'e bağlı değil. Önce push edin."));
        return ValidationResult.Ok();
    }

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken) =>
        ExecutionResult.FromCommand(await context.Repository.Remotes.PullAsync(mode, autostash: autostash, progress: progress, cancellationToken: cancellationToken).ConfigureAwait(false),
            conflictsPossible: true);
}

public sealed class PushOperation(string remote, string branch, bool setUpstream, bool force, IProgress<string>? progress) : IGitOperation
{
    private string? _remoteShaBefore;

    public string Kind => "push";
    public string Title => force ? Loc.T($"Force push {branch} to {remote}", $"Force push {branch} → {remote}") : Loc.T($"Push {branch} to {remote}", $"Push {branch} → {remote}");
    public string CommandPreview => $"git push{(setUpstream ? " -u" : "")}{(force ? " --force-with-lease" : "")} {remote} {branch}";
    public OperationRisk Risk => OperationRisk.Remote;
    public bool CapturesWorkingTree => false;
    public string? MetadataJson => JsonSerializer.Serialize(new { remote, branch, force, remoteShaBefore = _remoteShaBefore });

    public async Task<ValidationResult> ValidateAsync(OperationContext context, CancellationToken cancellationToken)
    {
        var refs = await context.Repository.Refs.GetRefTargetsAsync(cancellationToken, $"refs/remotes/{remote}/{branch}").ConfigureAwait(false);
        _remoteShaBefore = refs.Values.FirstOrDefault();
        if (force && _remoteShaBefore is null)
            return ValidationResult.Ok(Loc.T("ArChrono doesn't know the remote branch's current commit. Fetch first so force push can protect other people's work.", "ArChrono uzak branch'in güncel commit'ini bilmiyor. Force push'un başkalarının çalışmasını koruyabilmesi için önce fetch yapın."));
        return ValidationResult.Ok();
    }

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken)
    {
        // Force push her zaman --force-with-lease ile: bilinmeyen uzak commit'lerin üzerine yazılmaz.
        var lease = force ? _remoteShaBefore ?? string.Empty : null;
        var result = await context.Repository.Remotes.PushAsync(remote, RefNames.Branch(branch), branch, setUpstream, lease, progress, cancellationToken).ConfigureAwait(false);
        return ExecutionResult.FromCommand(result) with { MetadataJson = MetadataJson };
    }
}

/// <summary>Git Console'dan çalıştırılan serbest komut. Bilinmeyen etkiler için her zaman recovery point alınır.</summary>
public sealed class RawGitCommandOperation(IReadOnlyList<string> arguments) : IGitOperation
{
    public string Kind => "console";
    public string Title => "git " + string.Join(' ', arguments.Take(4)) + (arguments.Count > 4 ? " …" : "");
    public string CommandPreview => new GitCommand(".", arguments).DisplayText;
    public OperationRisk Risk => IsReadOnly(arguments) ? OperationRisk.Safe : OperationRisk.Reversible;
    public RefScope RefScope => RefScope.All;

    /// <summary>Durum değiştirmediği bilinen komutlar recovery point gerektirmez.</summary>
    public static bool IsReadOnly(IReadOnlyList<string> args) => args switch
    {
        ["status", ..] or ["log", ..] or ["show", ..] or ["diff", ..] or ["blame", ..] or ["shortlog", ..] or ["grep", ..] => true,
        ["ls-files", ..] or ["ls-tree", ..] or ["rev-parse", ..] or ["cat-file", ..] or ["describe", ..] or ["for-each-ref", ..] => true,
        ["help", ..] or ["version"] or ["--version"] or ["reflog"] or ["reflog", "show", ..] => true,
        ["stash", "list", ..] or ["stash", "show", ..] or ["worktree", "list", ..] => true,
        ["remote"] or ["remote", "-v"] or ["tag"] or ["tag", "-l" or "--list", ..] => true,
        ["branch"] or ["branch", "-a" or "-r" or "-v" or "-vv" or "--list"] => true,
        ["config", "--get" or "--list" or "-l", ..] => true,
        _ => false,
    };

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken)
    {
        var result = await context.Repository.Runner.RunAsync(new GitCommand(context.Repository.Info.RootPath, arguments), cancellationToken).ConfigureAwait(false);
        var execution = ExecutionResult.FromCommand(result, conflictsPossible: true);
        return execution with { Message = result.CombinedOutput };
    }
}

/// <summary>Time Machine: snapshot'tan dosyaları (veya tüm çalışma alanını) geri yükler. Geri yükleme de geri alınabilir.</summary>
public sealed class RestoreSnapshotOperation(SnapshotRecord snapshot, IReadOnlyCollection<string>? paths, Snapshots.WorkingTreeRestorer restorer) : IGitOperation
{
    public string Kind => "restore";
    public string Title => paths is { Count: 1 }
        ? Loc.T($"Restore {paths.First()} from {snapshot.CreatedAt:HH:mm:ss}", $"{paths.First()} dosyasını {snapshot.CreatedAt:HH:mm:ss} anından geri yükle")
        : paths is { Count: > 1 } ? Loc.T($"Restore {paths.Count} files from {snapshot.CreatedAt:HH:mm:ss}", $"{paths.Count} dosyayı {snapshot.CreatedAt:HH:mm:ss} anından geri yükle") : Loc.T($"Restore working tree from {snapshot.CreatedAt:HH:mm:ss}", $"Çalışma alanını {snapshot.CreatedAt:HH:mm:ss} anından geri yükle");
    public string CommandPreview => $"archrono restore-snapshot #{snapshot.Id}{(paths is null ? "" : " -- " + string.Join(' ', paths.Take(3)))}";
    public OperationRisk Risk => OperationRisk.Reversible;

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken)
    {
        var report = await restorer.RestoreAsync(context.Repository, snapshot, new Snapshots.RestoreOptions { OnlyPaths = paths }, cancellationToken).ConfigureAwait(false);
        return report.Failures.Count == 0
            ? ExecutionResult.Success(message: Loc.T($"{report.WrittenFiles} file(s) restored, {report.DeletedFiles} removed.", $"{report.WrittenFiles} dosya geri yüklendi, {report.DeletedFiles} dosya kaldırıldı."))
            : ExecutionResult.Failure(GitError.Simple(GitErrorCode.Unknown, Loc.T("Some files could not be restored.", "Bazı dosyalar geri yüklenemedi."), string.Join("\n", report.Failures.Take(10))));
    }
}

public sealed class SaveConflictResolutionOperation(string path, byte[] content) : IGitOperation
{
    public string Kind => "resolve-conflict";
    public string Title => Loc.T($"Save resolution for {path}", $"{path} için çözümü kaydet");
    public string CommandPreview => $"(write file) && git add -- {path}";
    public OperationRisk Risk => OperationRisk.Reversible;

    public async Task<ExecutionResult> ExecuteAsync(OperationContext context, CancellationToken cancellationToken) =>
        ExecutionResult.FromCommand(await context.Repository.Conflicts.SaveResolutionAsync(path, content, cancellationToken).ConfigureAwait(false));
}
