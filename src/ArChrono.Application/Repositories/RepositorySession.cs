using ArChrono.Application.Settings;
using ArChrono.Git;
using ArChrono.Git.Models;
using ArChrono.Git.Services;
using ArChrono.Platform;
using ArChrono.Recovery;
using ArChrono.Recovery.Operations;
using ArChrono.Recovery.Restore;
using ArChrono.Storage.Records;

namespace ArChrono.Application.Repositories;

public sealed record UndoState(GitOperationRecord? Undoable, GitOperationRecord? Redoable)
{
    public bool CanUndo => Undoable is not null;
    public bool CanRedo => Redoable is not null;
}

/// <summary>
/// Açık bir repository: Git servisleri, recovery context'i, dosya izleyici, Time Machine zamanlayıcısı.
/// UI yalnızca bu sınıf ve <see cref="GitActions"/> üzerinden repository ile konuşur.
/// </summary>
public sealed class RepositorySession : IAsyncDisposable
{
    private readonly RecoveryEngine _recovery;
    private readonly SettingsService _settings;
    private readonly RepositoryWatcher _watcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _timeMachineLoop;
    private readonly Task _autoFetchLoop;
    private int _dirty = 1;
    private int _autoFetchFailures;

    internal RepositorySession(GitRepository git, RepositoryRecord record, RecoveryEngine recovery, SettingsService settings)
    {
        Git = git;
        Record = record;
        _recovery = recovery;
        _settings = settings;
        Actions = new GitActions(this, recovery, settings);
        TimeMachine = new TimeMachine.TimeMachineService(this, recovery);
        Search = new Search.SearchService(this, recovery.Storage);

        _watcher = new RepositoryWatcher(git.Info.RootPath, git.Info.GitDir, git.Info.CommonDir);
        _watcher.Changed += OnWatcherChanged;
        _timeMachineLoop = Task.Run(() => TimeMachineLoopAsync(_lifetime.Token));
        _autoFetchLoop = Task.Run(() => AutoFetchLoopAsync(_lifetime.Token));
    }

    public GitRepository Git { get; }

    public RepositoryRecord Record { get; }

    public OperationContext Context => new(Git, Record.Id);

    public GitActions Actions { get; }

    public TimeMachine.TimeMachineService TimeMachine { get; }

    public Search.SearchService Search { get; }

    public string Name => Git.Info.Name;

    public string RootPath => Git.Info.RootPath;

    /// <summary>Dosya sistemi veya bir işlem repository'yi değiştirdi (UI yenilemesi için).</summary>
    public event EventHandler<RepositoryChange>? Changed;

    /// <summary>Uygulama içinden çalıştırılan her işlemin sonucu (toast, undo düğmesi).</summary>
    public event EventHandler<OperationOutcome>? OperationCompleted;

    public event EventHandler<SnapshotRecord>? SnapshotCaptured;

    public DateTimeOffset? LastSnapshotAt { get; private set; }

    public UndoState GetUndoState() => new(_recovery.GetUndoable(Record.Id), _recovery.GetRedoable(Record.Id));

    public Task<RestorePlan> PlanUndoAsync(GitOperationRecord operation, CancellationToken cancellationToken = default) =>
        _recovery.PlanUndoAsync(Context, operation, cancellationToken);

    public Task<RestorePlan> PlanRestoreAsync(RecoveryPointRecord point, RestoreMode mode, CancellationToken cancellationToken = default) =>
        _recovery.Planner.PlanRestoreAsync(Context, point, mode, cancellationToken);

    public async Task<OperationOutcome> ExecutePlanAsync(RestorePlan plan, GitOperationRecord? undoOf, CancellationToken cancellationToken = default)
    {
        var outcome = await _recovery.ExecutePlanAsync(Context, plan, undoOf, cancellationToken).ConfigureAwait(false);
        NotifyOperation(outcome);
        return outcome;
    }

    public Task<RecoveryPointRecord> CreateManualRecoveryPointAsync(string title, CancellationToken cancellationToken = default) =>
        _recovery.Points.CaptureAsync(Git, Record.Id, RecoveryPointKind.Manual, title, cancellationToken: cancellationToken);

    internal void NotifyOperation(OperationOutcome outcome)
    {
        Interlocked.Exchange(ref _dirty, 1);
        OperationCompleted?.Invoke(this, outcome);
        Changed?.Invoke(this, RepositoryChange.All);
    }

    internal void NotifySnapshot(SnapshotRecord snapshot)
    {
        LastSnapshotAt = snapshot.CreatedAt;
        SnapshotCaptured?.Invoke(this, snapshot);
    }

    public void RequestRefresh() => Changed?.Invoke(this, RepositoryChange.All);

    private void OnWatcherChanged(object? sender, RepositoryChange change)
    {
        if (change.HasFlag(RepositoryChange.WorkingTree) || change.HasFlag(RepositoryChange.Index)) Interlocked.Exchange(ref _dirty, 1);
        Changed?.Invoke(this, change);
    }

    private async Task TimeMachineLoopAsync(CancellationToken cancellationToken)
    {
        // İlk snapshot uygulama açıldığında: "açılıştaki durum" her zaman geri getirilebilir.
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            var settings = _settings.Current.TimeMachine;
            if (settings.Enabled && Record.TimeMachineEnabled && Interlocked.Exchange(ref _dirty, 0) == 1)
            {
                try
                {
                    await TimeMachine.CaptureAsync(SnapshotTrigger.Timer, null, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.Exchange(ref _dirty, 1);
                    Log.Error($"Time Machine snapshot failed for {Name}", ex);
                }
            }
            var interval = TimeSpan.FromMinutes(Math.Clamp(settings.IntervalMinutes, 1, 120));
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AutoFetchLoopAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            var git = _settings.Current.Git;
            if (git.AutoFetch && _autoFetchFailures < 3 && !Git.Status.GetState().IsBlocking())
            {
                try
                {
                    var remotes = await Git.Remotes.ListAsync(cancellationToken).ConfigureAwait(false);
                    if (remotes.Count > 0)
                    {
                        var result = await Git.Remotes.FetchAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                        _autoFetchFailures = result.Success ? 0 : _autoFetchFailures + 1;
                        if (result.Success) Changed?.Invoke(this, RepositoryChange.Refs);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _autoFetchFailures++;
                }
            }
            await Task.Delay(TimeSpan.FromMinutes(Math.Clamp(git.AutoFetchMinutes, 1, 240)), cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _watcher.Dispose();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_timeMachineLoop, _autoFetchLoop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        // Kapanışta son durum: kaydedilmemiş iş kaybolmasın.
        if (_settings.Current.TimeMachine.Enabled && Record.TimeMachineEnabled && _dirty == 1)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await TimeMachine.CaptureAsync(SnapshotTrigger.Shutdown, null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error($"Shutdown snapshot failed for {Name}", ex);
            }
        }
        _lifetime.Dispose();
    }
}

/// <summary>UI'ın kullandığı yüksek seviye Git işlemleri. Durum değiştiren her işlem Safe Git hattından geçer.</summary>
public sealed class GitActions(RepositorySession session, RecoveryEngine recovery, SettingsService settings)
{
    private GitRepository Git => session.Git;

    public async Task<OperationOutcome> RunAsync(IGitOperation operation, CancellationToken cancellationToken = default)
    {
        var outcome = await recovery.RunAsync(session.Context, operation, cancellationToken).ConfigureAwait(false);
        session.NotifyOperation(outcome);
        return outcome;
    }

    // Stage/unstage veri kaybettirmez; recovery point gerektirmeden doğrudan çalışır.
    public async Task StageAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0) return;
        (await Git.WorkingTree.StageAsync(paths, cancellationToken).ConfigureAwait(false)).EnsureSuccess();
        session.RequestRefresh();
    }

    public async Task UnstageAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0) return;
        var head = await Git.Refs.GetHeadAsync(cancellationToken).ConfigureAwait(false);
        (await Git.WorkingTree.UnstageAsync(paths, !head.IsUnborn, cancellationToken).ConfigureAwait(false)).EnsureSuccess();
        session.RequestRefresh();
    }

    public async Task StageAllAsync(CancellationToken cancellationToken = default)
    {
        (await Git.WorkingTree.StageAllAsync(cancellationToken).ConfigureAwait(false)).EnsureSuccess();
        session.RequestRefresh();
    }

    public async Task UnstageAllAsync(CancellationToken cancellationToken = default)
    {
        var head = await Git.Refs.GetHeadAsync(cancellationToken).ConfigureAwait(false);
        (await Git.WorkingTree.UnstageAllAsync(!head.IsUnborn, cancellationToken).ConfigureAwait(false)).EnsureSuccess();
        session.RequestRefresh();
    }

    public async Task ApplyPatchToIndexAsync(string patch, bool reverse, CancellationToken cancellationToken = default)
    {
        (await Git.WorkingTree.ApplyPatchAsync(patch, toIndex: true, reverse, cancellationToken).ConfigureAwait(false)).EnsureSuccess();
        session.RequestRefresh();
    }

    public Task<OperationOutcome> CommitAsync(string message, bool amend) => RunAsync(new CommitOperation(message, amend));

    public Task<OperationOutcome> CreateBranchAsync(string name, string? startPoint, bool checkout) => RunAsync(new CreateBranchOperation(name, startPoint, checkout));

    public Task<OperationOutcome> DeleteBranchAsync(RefInfo branch) => RunAsync(new DeleteBranchOperation(branch.Name, force: true));

    public Task<OperationOutcome> RenameBranchAsync(RefInfo branch, string newName) => RunAsync(new RenameBranchOperation(branch.Name, newName));

    public Task<OperationOutcome> CheckoutAsync(RefInfo reference) => reference.Kind == RefKind.RemoteBranch
        ? RunAsync(CheckoutOperation.RemoteBranch(reference.Name, reference.DisplayName))
        : RunAsync(CheckoutOperation.Branch(reference.Name));

    public Task<OperationOutcome> CheckoutCommitAsync(string sha) => RunAsync(CheckoutOperation.Detached(sha));

    public Task<OperationOutcome> MergeAsync(string revision, FastForwardMode mode = FastForwardMode.Auto) => RunAsync(new MergeOperation(revision, mode));

    public Task<OperationOutcome> RebaseAsync(string upstream) => RunAsync(new RebaseOperation(upstream, settings.Current.Git.Autostash));

    public Task<OperationOutcome> InteractiveRebaseAsync(string? baseRevision, IReadOnlyList<RebaseTodoItem> items) =>
        RunAsync(new InteractiveRebaseOperation(baseRevision, items, settings.Current.Git.Autostash));

    public Task<OperationOutcome> CherryPickAsync(IReadOnlyList<CommitInfo> commits) => RunAsync(new CherryPickOperation(commits));

    public Task<OperationOutcome> RevertAsync(CommitInfo commit) => RunAsync(new RevertOperation(commit));

    public Task<OperationOutcome> ResetAsync(string revision, ResetMode mode, string? branchName) => RunAsync(new ResetOperation(revision, mode, branchName));

    public Task<OperationOutcome> StashAsync(string? message, bool includeUntracked = true) => RunAsync(new StashPushOperation(message, includeUntracked));

    public Task<OperationOutcome> ApplyStashAsync(StashEntry stash, bool pop) => RunAsync(new StashApplyOperation(stash, pop));

    public Task<OperationOutcome> DropStashAsync(StashEntry stash) => RunAsync(new StashDropOperation(stash));

    public Task<OperationOutcome> DiscardAsync(IReadOnlyCollection<StatusEntry> entries, bool includeStaged)
    {
        var untracked = entries.Where(e => e.IsUntracked).Select(e => e.Path).ToList();
        var tracked = entries.Where(e => !e.IsUntracked).Select(e => e.Path).ToList();
        return RunAsync(new DiscardChangesOperation(tracked, untracked, includeStaged));
    }

    public Task<OperationOutcome> CreateTagAsync(string name, string revision, string? message) => RunAsync(new TagCreateOperation(name, revision, message));

    public Task<OperationOutcome> DeleteTagAsync(RefInfo tag) => RunAsync(new TagDeleteOperation(tag.Name));

    public Task<OperationOutcome> AbortAsync() => RunAsync(new AbortOperation(Git.Status.GetState()));

    public Task<OperationOutcome> ContinueAsync() => RunAsync(new ContinueOperation(Git.Status.GetState()));

    public Task<OperationOutcome> ResolveConflictAsync(ConflictFile conflict, bool useOurs) => RunAsync(new ResolveConflictOperation(conflict, useOurs));

    public Task<OperationOutcome> SaveConflictResolutionAsync(string path, byte[] content) => RunAsync(new SaveConflictResolutionOperation(path, content));

    public Task<OperationOutcome> FetchAsync(IProgress<string>? progress = null) => RunAsync(new FetchOperation(null, progress));

    public Task<OperationOutcome> PullAsync(IProgress<string>? progress = null) =>
        RunAsync(new PullOperation(settings.Current.Git.PullMode, settings.Current.Git.Autostash, progress));

    public Task<OperationOutcome> PushAsync(string remote, string branch, bool setUpstream, bool force, IProgress<string>? progress = null) =>
        RunAsync(new PushOperation(remote, branch, setUpstream, force, progress));

    public Task<OperationOutcome> RunConsoleCommandAsync(IReadOnlyList<string> arguments) => RunAsync(new RawGitCommandOperation(arguments));

    public Task<OperationOutcome> RestoreSnapshotAsync(SnapshotRecord snapshot, IReadOnlyCollection<string>? paths) =>
        RunAsync(new RestoreSnapshotOperation(snapshot, paths, recovery.Restorer));
}
