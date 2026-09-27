using System.Collections.ObjectModel;
using ArChrono.App.Controls;
using ArChrono.App.Infrastructure;
using ArChrono.App.ViewModels.Dialogs;
using ArChrono.Application.Repositories;
using ArChrono.Git.Errors;
using ArChrono.Git.Models;
using ArChrono.Git.Services;
using ArChrono.Localization;
using ArChrono.Platform.Shell;
using ArChrono.Recovery.Operations;
using ArChrono.Recovery.Restore;
using ArChrono.Storage.Records;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArChrono.App.ViewModels;

public enum RepositorySection
{
    Workspace,
    Recovery,
    TimeMachine,
}

/// <summary>
/// Açık repository'nin kabuğu: üst çubuk, navigasyon, Chrono Strip ve tüm Git işlemlerinin tek UI giriş noktası.
/// Tehlikeli işlemler burada onaylanır; sonuçlar standart biçimde (toast + Undo) sunulur.
/// </summary>
public sealed partial class RepositoryViewModel : ViewModelBase
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private int _pendingChange;
    private bool _historyLoaded;
    private bool _detached;

    public RepositoryViewModel(MainWindowViewModel shell, RepositorySession session)
    {
        Shell = shell;
        Session = session;
        Workspace = new WorkspaceViewModel(this);
        Recovery = new RecoveryCenterViewModel(this);
        TimeMachine = new TimeMachineViewModel(this);
        Console = new ConsoleViewModel(this);
        session.Changed += OnSessionChanged;
        session.SnapshotCaptured += OnSnapshotCaptured;
    }

    public MainWindowViewModel Shell { get; }
    public RepositorySession Session { get; }
    public WorkspaceViewModel Workspace { get; }
    public RecoveryCenterViewModel Recovery { get; }
    public TimeMachineViewModel TimeMachine { get; }
    public ConsoleViewModel Console { get; }

    public string Name => Session.Name;
    public string RootPath => Session.RootPath;
    public bool IsPro => Shell.IsPro;
    public bool IsGuided => !Shell.IsPro;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorkspace), nameof(IsRecovery), nameof(IsTimeMachine))]
    public partial RepositorySection Section { get; set; }

    public bool IsWorkspace
    {
        get => Section == RepositorySection.Workspace;
        set { if (value) Section = RepositorySection.Workspace; }
    }

    public bool IsRecovery
    {
        get => Section == RepositorySection.Recovery;
        set { if (value) Section = RepositorySection.Recovery; }
    }

    public bool IsTimeMachine
    {
        get => Section == RepositorySection.TimeMachine;
        set { if (value) Section = RepositorySection.TimeMachine; }
    }

    [ObservableProperty]
    public partial bool IsConsoleOpen { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string BusyText { get; set; } = string.Empty;

    // --- Repository durumu ---
    [ObservableProperty]
    public partial RepositoryStatus? Status { get; set; }

    [ObservableProperty]
    public partial HeadInfo Head { get; set; } = new(null, null);

    [ObservableProperty]
    public partial IReadOnlyList<RefInfo> Refs { get; set; } = [];

    public string BranchDisplay => Head.BranchName ?? (Head.Sha is { } sha ? (IsPro ? Loc.T($"detached at {sha[..7]}", $"{sha[..7]} üzerinde (detached)") : Loc.T($"viewing {sha[..7]} (not on a branch)", $"{sha[..7]} görüntüleniyor (branch dışında)")) : Loc.T("no commits yet", "henüz commit yok"));
    public int Ahead => Status?.Branch.Ahead ?? 0;
    public int Behind => Status?.Branch.Behind ?? 0;
    public bool HasUpstream => Status?.Branch.Upstream is not null;
    public bool HasAhead => Ahead > 0;
    public bool HasBehind => Behind > 0;
    public bool HasConflicts => ConflictCount > 0;
    public string SyncHint => !HasUpstream ? (IsPro ? Loc.T("No upstream", "Upstream yok") : Loc.T("Not on the server yet", "Henüz sunucuda değil"))
        : Ahead == 0 && Behind == 0 ? (IsPro ? Loc.T("Up to date", "Güncel") : Loc.T("In sync with the server", "Sunucuyla eşit"))
        : IsPro ? $"↑{Ahead} ↓{Behind}" : Loc.T($"{Ahead} to send · {Behind} to get", $"{Ahead} gönderilecek · {Behind} alınacak");
    public RepositoryState State => Status?.State ?? RepositoryState.Clean;
    public bool IsOperationInProgress => State.IsBlocking();
    public int ConflictCount => Status?.ConflictedCount ?? 0;
    public string StateTitle => State switch
    {
        RepositoryState.Merging => IsPro ? Loc.T("Merge in progress", "Merge sürüyor") : Loc.T("Combining changes", "Değişiklikler birleştiriliyor"),
        RepositoryState.Rebasing => IsPro ? Loc.T("Rebase in progress", "Rebase sürüyor") : Loc.T("Moving commits", "Commit'ler taşınıyor"),
        RepositoryState.CherryPicking => IsPro ? Loc.T("Cherry-pick in progress", "Cherry-pick sürüyor") : Loc.T("Copying a commit", "Bir commit kopyalanıyor"),
        RepositoryState.Reverting => IsPro ? Loc.T("Revert in progress", "Revert sürüyor") : Loc.T("Undoing a commit", "Bir commit geri alınıyor"),
        RepositoryState.ApplyingPatches => Loc.T("Applying patches", "Patch'ler uygulanıyor"),
        _ => string.Empty,
    };
    public string StateDetail => ConflictCount > 0
        ? Loc.T($"{ConflictCount} file(s) need your decision. Nothing is lost — you can also undo.", $"{ConflictCount} dosya kararınızı bekliyor. Hiçbir şey kaybolmadı — geri de alabilirsiniz.")
        : Loc.T("All conflicts are resolved. Continue to finish, or abort to go back.", "Tüm çakışmalar çözüldü. Bitirmek için devam edin ya da geri dönmek için iptal edin.");

    // --- Undo ---
    [ObservableProperty]
    public partial UndoState UndoState { get; set; } = new(null, null);

    public bool CanUndo => UndoState.CanUndo && !IsBusy;
    public bool CanRedo => UndoState.CanRedo && !IsBusy;
    public string UndoTitle => UndoState.Undoable is { } op ? OperationTitles.UndoPrefix + ShortTitle(op.Title) : Loc.T("Nothing to undo", "Geri alınacak bir şey yok");
    public string RedoTitle => UndoState.Redoable is { } op ? OperationTitles.RedoPrefix + ShortTitle(OperationTitles.StripUndoPrefix(op.Title)) : Loc.T("Nothing to redo", "Yinelenecek bir şey yok");

    // --- Chrono Strip ---
    [ObservableProperty]
    public partial IReadOnlyList<ChronoMark> ChronoMarks { get; set; } = [];

    [ObservableProperty]
    public partial DateTimeOffset ChronoStart { get; set; } = DateTimeOffset.Now.AddHours(-8);

    [ObservableProperty]
    public partial DateTimeOffset ChronoEnd { get; set; } = DateTimeOffset.Now;

    [ObservableProperty]
    public partial string ChronoSummary { get; set; } = string.Empty;

    private static string ShortTitle(string title) => title.Length > 38 ? title[..36] + "…" : title;

    public async Task InitializeAsync()
    {
        await RefreshAsync(RepositoryChange.All);
        Workspace.SelectInitialRow();
    }

    public void Detach()
    {
        _detached = true;
        Session.Changed -= OnSessionChanged;
        Session.SnapshotCaptured -= OnSnapshotCaptured;
        Console.Detach();
    }

    public void OnModeChanged()
    {
        foreach (var name in new[] { nameof(IsPro), nameof(IsGuided), nameof(BranchDisplay), nameof(SyncHint), nameof(StateTitle) })
            OnPropertyChanged(name);
        Workspace.OnModeChanged();
    }

    private void OnSessionChanged(object? sender, RepositoryChange change)
    {
        if (_detached) return;
        Interlocked.Or(ref _pendingChange, (int)change);
        OnUi(() => _ = RefreshAsync(RepositoryChange.None));
    }

    private void OnSnapshotCaptured(object? sender, SnapshotRecord snapshot) => OnUi(() =>
    {
        RefreshChrono();
        TimeMachine.MarkStale();
    });

    /// <summary>Durum, ref'ler ve (gerekirse) geçmişi arka planda yükler; UI'ı tek seferde günceller.</summary>
    public async Task RefreshAsync(RepositoryChange change)
    {
        await _refreshGate.WaitAsync();
        try
        {
            change |= (RepositoryChange)Interlocked.Exchange(ref _pendingChange, 0);
            if (change == RepositoryChange.None || _detached) return;

            var git = Session.Git;
            var loaded = await Task.Run(async () =>
            {
                var status = git.Status.GetStatusAsync();
                var head = git.Refs.GetHeadAsync();
                var refs = git.Refs.GetRefsAsync();
                var stashes = git.Stash.ListAsync();
                await Task.WhenAll(status, head, refs, stashes);
                return (Status: await status, Head: await head, Refs: await refs, Stashes: await stashes);
            });

            Status = loaded.Status;
            Head = loaded.Head;
            Refs = loaded.Refs;
            var reloadHistory = !_historyLoaded || change.HasFlag(RepositoryChange.Refs) || change.HasFlag(RepositoryChange.State);
            await Workspace.UpdateAsync(loaded.Status, loaded.Refs, loaded.Stashes, loaded.Head, reloadHistory);
            _historyLoaded = true;

            UndoState = Session.GetUndoState();
            RefreshChrono();
            Recovery.MarkStale();
            NotifyStateProperties();
        }
        catch (GitException ex)
        {
            Shell.ShowToast(new ToastViewModel(Loc.T("Could not refresh the repository.", "Depo yenilenemedi."), ToastKind.Warning, ex.Error.Title));
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void NotifyStateProperties()
    {
        foreach (var name in new[]
                 {
                     nameof(BranchDisplay), nameof(Ahead), nameof(Behind), nameof(HasAhead), nameof(HasBehind), nameof(HasConflicts), nameof(HasUpstream), nameof(SyncHint), nameof(State),
                     nameof(IsOperationInProgress), nameof(ConflictCount), nameof(StateTitle), nameof(StateDetail),
                     nameof(CanUndo), nameof(CanRedo), nameof(UndoTitle), nameof(RedoTitle),
                 })
            OnPropertyChanged(name);
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    public void RefreshChrono()
    {
        var storage = Shell.Services.Storage;
        var end = DateTimeOffset.Now.AddMinutes(10);
        var start = DateTimeOffset.Now.AddHours(-8);
        var marks = new List<ChronoMark>();
        var operations = storage.Operations.List(Session.Record.Id, 300).ToDictionary(o => o.Id);
        foreach (var point in storage.RecoveryPoints.List(Session.Record.Id, 300).Where(p => p.CreatedAt >= start && p.Kind != RecoveryPointKind.AfterOperation))
            marks.Add(new ChronoMark(point.CreatedAt, ChronoMarkKind.RecoveryPoint, point.Title, point));
        foreach (var op in operations.Values.Where(o => o.Status == OperationStatus.Failed && o.StartedAt >= start))
            marks.Add(new ChronoMark(op.StartedAt, ChronoMarkKind.Failed, Loc.T("Failed: ", "Başarısız: ") + op.Title, op));
        var snapshots = storage.Snapshots.List(Session.Record.Id, start, null, 500).Where(s => s.Trigger != SnapshotTrigger.Operation).ToList();
        foreach (var snapshot in snapshots)
            marks.Add(new ChronoMark(snapshot.CreatedAt, ChronoMarkKind.Snapshot, snapshot.Label ?? Loc.T($"Working tree snapshot · {snapshot.EntryCount} file(s)", $"Çalışma alanı anlık görüntüsü · {snapshot.EntryCount} dosya"), snapshot));

        ChronoStart = start;
        ChronoEnd = end;
        ChronoMarks = marks;

        var latest = storage.Snapshots.GetLatest(Session.Record.Id);
        var used = storage.Blobs.GetTotalStoredBytes();
        var limit = (long)(Shell.Services.Settings.Current.TimeMachine.MaxStorageGigabytes * 1024 * 1024 * 1024);
        var tm = Shell.Services.Settings.Current.TimeMachine.Enabled ? "" : Loc.T("Time Machine is off · ", "Zaman Makinesi kapalı · ");
        ChronoSummary = tm + (latest is null ? Loc.T("No snapshots yet", "Henüz anlık görüntü yok") : Loc.T("Last snapshot ", "Son anlık görüntü: ") + Format.Relative(latest.CreatedAt)) + $" · {Format.Bytes(used)} / {Format.Bytes(limit)}";
    }

    public void OnChronoMarkClicked(ChronoMark mark)
    {
        switch (mark.Payload)
        {
            case RecoveryPointRecord point:
                Section = RepositorySection.Recovery;
                _ = Recovery.SelectPointAsync(point.Id);
                break;
            case SnapshotRecord snapshot:
                Section = RepositorySection.TimeMachine;
                _ = TimeMachine.SelectSnapshotAsync(snapshot);
                break;
            case GitOperationRecord operation when operation.BeforePointId is { } pointId:
                Section = RepositorySection.Recovery;
                _ = Recovery.SelectPointAsync(pointId);
                break;
        }
    }

    // ------------------------------------------------------------------
    // İşlem hattı
    // ------------------------------------------------------------------

    public async Task<OperationOutcome?> RunAsync(Func<Task<OperationOutcome>> run, string busyText, ConfirmOperationDialogViewModel? confirm = null,
        string? successMessage = null)
    {
        if (confirm is not null && await Shell.ShowDialogAsync(confirm) is not { Accepted: true }) return null;
        IsBusy = true;
        BusyText = busyText;
        NotifyStateProperties();
        OperationOutcome? outcome = null;
        try
        {
            outcome = await Task.Run(run);
        }
        catch (GitException ex)
        {
            await Shell.ShowErrorAsync(ex.Error);
        }
        finally
        {
            IsBusy = false;
            NotifyStateProperties();
        }

        if (outcome is not null)
        {
            await RefreshAsync(RepositoryChange.All);
            await Shell.PresentOutcomeAsync(outcome,
                undo: () => UndoOperationAsync(outcome.Operation),
                viewRecoveryPoint: outcome.Before is { } before ? () => ShowRecoveryPointAsync(before.Id) : null,
                resolve: () =>
                {
                    Section = RepositorySection.Workspace;
                    Workspace.ShowConflicts();
                    return Task.CompletedTask;
                },
                successMessage: successMessage);
        }
        return outcome;
    }

    public Task ShowRecoveryPointAsync(long pointId)
    {
        Section = RepositorySection.Recovery;
        return Recovery.SelectPointAsync(pointId);
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private Task Undo() => UndoState.Undoable is { } op ? UndoOperationAsync(op) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private Task Redo() => UndoState.Redoable is { } op ? UndoOperationAsync(op) : Task.CompletedTask;

    /// <summary>
    /// Bir işlemi geri alır. Plan uyarısızsa doğrudan uygulanır (Tower tarzı hızlı undo);
    /// uyarı veya uzak adım varsa önce plan gösterilir.
    /// </summary>
    public async Task UndoOperationAsync(GitOperationRecord operation, bool alwaysPreview = false)
    {
        var fresh = Shell.Services.Storage.Operations.Get(operation.Id) ?? operation;
        if (fresh.UndoneByOperationId is not null)
        {
            Shell.ShowToast(new ToastViewModel(Loc.T("This operation was already undone.", "Bu işlem zaten geri alınmış."), ToastKind.Info));
            return;
        }

        RestorePlan plan;
        try
        {
            plan = await Task.Run(() => Session.PlanUndoAsync(fresh));
        }
        catch (GitException ex)
        {
            await Shell.ShowErrorAsync(ex.Error);
            return;
        }

        if (plan.IsEmpty)
        {
            Shell.ShowToast(new ToastViewModel(plan.Notes.FirstOrDefault() ?? Loc.T("There is nothing to undo.", "Geri alınacak bir şey yok."), ToastKind.Info));
            return;
        }

        var needsPreview = alwaysPreview || plan.Actions.Any(a => a.Warning is not null || a.IsRemote);
        if (needsPreview)
        {
            var confirmed = await Shell.ShowDialogAsync(new RestorePlanDialogViewModel(plan, IsPro, confirmText: fresh.Kind == "undo" ? Loc.T("Redo", "Yinele") : Loc.T("Undo", "Geri al")));
            if (confirmed is null) return;
            plan = confirmed;
        }

        await RunAsync(() => Session.ExecutePlanAsync(plan, fresh), plan.Title + "…",
            successMessage: (fresh.Kind == "undo" ? Loc.T("Redone: ", "Yinelendi: ") : Loc.T("Undone: ", "Geri alındı: ")) + fresh.Title);
    }

    public async Task RestoreToPointAsync(RecoveryPointRecord point, RestoreMode mode)
    {
        RestorePlan plan;
        try
        {
            plan = await Task.Run(() => Session.PlanRestoreAsync(point, mode));
        }
        catch (GitException ex)
        {
            await Shell.ShowErrorAsync(ex.Error);
            return;
        }

        var confirmed = await Shell.ShowDialogAsync(new RestorePlanDialogViewModel(plan, IsPro, m => Task.Run(() => Session.PlanRestoreAsync(point, m))));
        if (confirmed is null) return;
        await RunAsync(() => Session.ExecutePlanAsync(confirmed, null), Loc.T("Restoring…", "Geri yükleniyor…"), successMessage: Loc.T("Restored to ", "Geri dönüldü: ") + point.CreatedAt.ToString("HH:mm:ss"));
    }

    // --- Sync ---
    [RelayCommand]
    private Task Fetch() => RunAsync(() => Session.Actions.FetchAsync(new Progress<string>(l => OnUi(() => BusyText = l))), Loc.T("Fetching…", "Fetch yapılıyor…"),
        successMessage: IsPro ? Loc.T("Fetched", "Fetch tamamlandı") : Loc.T("Checked the server for new changes", "Sunucuda yeni değişiklikler kontrol edildi"));

    [RelayCommand]
    private Task Pull() => RunAsync(() => Session.Actions.PullAsync(new Progress<string>(l => OnUi(() => BusyText = l))), Loc.T("Pulling…", "Pull yapılıyor…"),
        successMessage: IsPro ? Loc.T("Pulled", "Pull tamamlandı") : Loc.T("Got the latest changes", "Son değişiklikler alındı"));

    [RelayCommand]
    private async Task Push()
    {
        if (Head.BranchName is not { } branch)
        {
            Shell.ShowToast(new ToastViewModel(Loc.T("Switch to a branch before pushing.", "Push etmeden önce bir branch'e geçin."), ToastKind.Warning));
            return;
        }
        var (remote, remoteBranch, setUpstream) = await ResolvePushTargetAsync(branch);
        if (remote is null)
        {
            Shell.ShowToast(new ToastViewModel(Loc.T("This repository has no remote yet.", "Bu deponun henüz bir remote'u yok."), ToastKind.Warning, Loc.T("Add one with: git remote add origin <url>", "Eklemek için: git remote add origin <url>")));
            return;
        }
        await RunAsync(() => Session.Actions.PushAsync(remote, remoteBranch, setUpstream, force: false, new Progress<string>(l => OnUi(() => BusyText = l))),
            Loc.T("Pushing…", "Push yapılıyor…"), successMessage: IsPro ? Loc.T($"Pushed {branch} to {remote}", $"{branch} → {remote} push edildi") : Loc.T("Sent your commits to the server", "Commit'leriniz sunucuya gönderildi"));
    }

    public async Task ForcePushAsync(RefInfo branch)
    {
        var (remote, remoteBranch, setUpstream) = await ResolvePushTargetAsync(branch.Name);
        if (remote is null) return;
        var confirm = new ConfirmOperationDialogViewModel("FORCE PUSH (WITH LEASE)", Loc.T("Replace the branch on the server", "Sunucudaki branch'in yerine koy"),
        [
            Loc.T($"{remote}/{remoteBranch} on the server will be replaced by your local {branch.Name}.", $"Sunucudaki {remote}/{remoteBranch}, yereldeki {branch.Name} ile değiştirilecek."),
            Loc.T("Commits that exist only on the server will no longer be on that branch.", "Yalnızca sunucuda olan commit'ler artık o branch'te olmayacak."),
            Loc.T("The push is refused if someone else pushed since your last fetch (force-with-lease).", "Son fetch'inizden sonra başka biri push ettiyse push reddedilir (force-with-lease)."),
        ], $"git push --force-with-lease {remote} {branch.Name}", IsPro, confirmText: "Force push");
        await RunAsync(() => Session.Actions.PushAsync(remote, remoteBranch, setUpstream, force: true), Loc.T("Force pushing…", "Force push yapılıyor…"), confirm);
    }

    private async Task<(string? Remote, string Branch, bool SetUpstream)> ResolvePushTargetAsync(string branch)
    {
        var local = Refs.FirstOrDefault(r => r.Kind == RefKind.LocalBranch && r.Name == branch);
        if (local?.Upstream is { } upstream && upstream.StartsWith(RefNames.RemotesPrefix, StringComparison.Ordinal))
        {
            var rest = upstream[RefNames.RemotesPrefix.Length..];
            var slash = rest.IndexOf('/');
            return (rest[..slash], rest[(slash + 1)..], false);
        }
        var remotes = await Session.Git.Remotes.ListAsync();
        var remote = remotes.FirstOrDefault(r => r.Name == "origin") ?? remotes.FirstOrDefault();
        return (remote?.Name, branch, true);
    }

    // --- Branch işlemleri ---
    [RelayCommand]
    public async Task CreateBranch(string? startPoint)
    {
        var dialog = new TextInputDialogViewModel(
            IsPro ? Loc.T("Create branch", "Branch oluştur") : Loc.T("Create a branch", "Branch oluştur"),
            IsPro ? Loc.T($"New branch from {(startPoint is null ? BranchDisplay : startPoint[..Math.Min(7, startPoint.Length)])}.", $"{(startPoint is null ? BranchDisplay : startPoint[..Math.Min(7, startPoint.Length)])} noktasından yeni branch.")
                  : Loc.T("A branch is a separate line of work. Your current changes come with you.", "Branch ayrı bir çalışma hattıdır. Mevcut değişiklikleriniz sizinle gelir."),
            Loc.T("Branch name", "Branch adı"), confirmText: Loc.T("Create", "Oluştur"), optionText: Loc.T("Switch to the new branch", "Yeni branch'e geç"), optionDefault: true,
            validate: async name => await Session.Git.Refs.IsValidBranchNameAsync(name) ? null : Loc.T("Use letters, numbers, '-', '_' and '/'. No spaces.", "Harf, rakam, '-', '_' ve '/' kullanın. Boşluk olmasın."));
        var result = await Shell.ShowDialogAsync(dialog);
        if (result is null) return;
        await RunAsync(() => Session.Actions.CreateBranchAsync(result.Text, startPoint, result.Option), Loc.T("Creating branch…", "Branch oluşturuluyor…"));
    }

    public Task CheckoutAsync(RefInfo reference) =>
        RunAsync(() => Session.Actions.CheckoutAsync(reference), Loc.T($"Switching to {reference.DisplayName}…", $"{reference.DisplayName} branch'ine geçiliyor…"),
            successMessage: IsPro ? Loc.T($"Switched to {reference.DisplayName}", $"{reference.DisplayName} branch'ine geçildi") : Loc.T($"You are now working on {reference.DisplayName}", $"Artık {reference.DisplayName} üzerinde çalışıyorsunuz"));

    public Task CheckoutCommitAsync(CommitInfo commit)
    {
        var confirm = new ConfirmOperationDialogViewModel("CHECKOUT (DETACHED HEAD)", Loc.T("View an old version", "Eski bir sürümü görüntüle"),
        [
            Loc.T($"Your files will show the project as it was at \"{commit.Subject}\".", $"Dosyalarınız projeyi \"{commit.Subject}\" anındaki hâliyle gösterecek."),
            Loc.T("You won't be on a branch. New commits here should go on a new branch.", "Bir branch üzerinde olmayacaksınız. Burada yapılacak yeni commit'ler yeni bir branch'e gitmeli."),
        ], $"git switch --detach {commit.ShortSha}", IsPro, confirmText: Loc.T("Switch", "Geç"), isDanger: false);
        return RunAsync(() => Session.Actions.CheckoutCommitAsync(commit.Sha), Loc.T("Switching…", "Geçiliyor…"), confirm);
    }

    public async Task DeleteBranchAsync(RefInfo branch)
    {
        var unique = await Session.Git.History.CountAheadBehindAsync(branch.FullName, "HEAD");
        var effects = new List<string> { Loc.T($"The branch {branch.Name} will be removed.", $"{branch.Name} branch'i kaldırılacak.") };
        if (unique.Ahead > 0) effects.Add(Loc.T($"{unique.Ahead} commit(s) exist only on this branch. They stay recoverable in the Recovery Center.", $"{unique.Ahead} commit yalnızca bu branch'te var. Kurtarma Merkezi'nden geri getirilebilir kalırlar."));
        var confirm = new ConfirmOperationDialogViewModel(Loc.T("DELETE BRANCH", "BRANCH SİL"), Loc.T("Delete this branch", "Bu branch'i sil"), effects, $"git branch -D {branch.Name}", IsPro, confirmText: Loc.T("Delete", "Sil"));
        await RunAsync(() => Session.Actions.DeleteBranchAsync(branch), Loc.T("Deleting branch…", "Branch siliniyor…"), confirm);
    }

    public async Task RenameBranchAsync(RefInfo branch)
    {
        var dialog = new TextInputDialogViewModel(Loc.T("Rename branch", "Branch'i yeniden adlandır"), Loc.T($"Rename {branch.Name}.", $"{branch.Name} branch'inin adını değiştirin."), Loc.T("New name", "Yeni ad"), branch.Name, Loc.T("Rename", "Yeniden adlandır"),
            validate: async name => await Session.Git.Refs.IsValidBranchNameAsync(name) ? null : Loc.T("That name is not valid.", "Bu ad geçerli değil."));
        if (await Shell.ShowDialogAsync(dialog) is not { } result || result.Text == branch.Name) return;
        await RunAsync(() => Session.Actions.RenameBranchAsync(branch, result.Text), Loc.T("Renaming…", "Yeniden adlandırılıyor…"));
    }

    public async Task IntegrateAsync(RefInfo source, IntegrationKind initial)
    {
        if (Head.BranchName is not { } target)
        {
            Shell.ShowToast(new ToastViewModel(Loc.T("Switch to the branch you want to update first.", "Önce güncellemek istediğiniz branch'e geçin."), ToastKind.Warning));
            return;
        }
        var dialog = new IntegrateDialogViewModel(source.Name, target, IsPro, initial,
            () => Task.Run<ConflictPrediction?>(async () => await Session.Git.Integration.PredictMergeAsync("HEAD", source.FullName)));
        if (await Shell.ShowDialogAsync(dialog) is not { } choice) return;

        if (choice.Kind == IntegrationKind.Rebase)
        {
            var confirm = new ConfirmOperationDialogViewModel("REBASE", Loc.T("Move my commits on top of " + source.Name, "Commit'lerimi " + source.Name + " üzerine taşı"),
            [
                Loc.T($"Commits on {target} that are not in {source.Name} will be recreated on top of it.", $"{target} üzerinde olup {source.Name} içinde olmayan commit'ler onun üzerine yeniden oluşturulacak."),
                Loc.T("Commit ids change. If this branch is already shared, others will need to reset.", "Commit kimlikleri değişir. Bu branch zaten paylaşıldıysa diğerlerinin reset yapması gerekir."),
            ], $"git rebase {source.Name}", IsPro, confirmText: "Rebase");
            await RunAsync(() => Session.Actions.RebaseAsync(source.FullName), Loc.T("Rebasing…", "Rebase yapılıyor…"), confirm);
        }
        else
        {
            await RunAsync(() => Session.Actions.MergeAsync(source.FullName, choice.FastForward), Loc.T("Merging…", "Merge yapılıyor…"),
                successMessage: IsPro ? Loc.T($"Merged {source.Name} into {target}", $"{source.Name}, {target} içine merge edildi") : Loc.T($"Combined {source.Name} into {target}", $"{source.Name}, {target} ile birleştirildi"));
        }
    }

    // --- Commit işlemleri ---
    public async Task ResetToAsync(CommitInfo commit)
    {
        if (await Shell.ShowDialogAsync(new ResetDialogViewModel(commit, Head.BranchName ?? "HEAD", IsPro)) is not { } choice) return;
        if (choice.Mode == ResetMode.Hard)
        {
            var changed = Status?.Entries.Count ?? 0;
            var confirm = new ConfirmOperationDialogViewModel("RESET --HARD", Loc.T("Throw away changes and go back", "Değişiklikleri at ve geri dön"),
            [
                Loc.T($"{Head.BranchName ?? "HEAD"} will move to {commit.ShortSha} \"{commit.Subject}\".", $"{Head.BranchName ?? "HEAD"}, {commit.ShortSha} \"{commit.Subject}\" commit'ine taşınacak."),
                changed > 0 ? Loc.T($"{changed} uncommitted change(s) will be overwritten.", $"Commit edilmemiş {changed} değişikliğin üzerine yazılacak.") : Loc.T("Commits after it will no longer be on this branch.", "Ondan sonraki commit'ler artık bu branch'te olmayacak."),
            ], $"git reset --hard {commit.ShortSha}", IsPro, confirmText: "Reset");
            await RunAsync(() => Session.Actions.ResetAsync(commit.Sha, ResetMode.Hard, Head.BranchName), Loc.T("Resetting…", "Reset yapılıyor…"), confirm);
        }
        else
        {
            await RunAsync(() => Session.Actions.ResetAsync(commit.Sha, choice.Mode, Head.BranchName), Loc.T("Resetting…", "Reset yapılıyor…"));
        }
    }

    public Task CherryPickAsync(CommitInfo commit) =>
        RunAsync(() => Session.Actions.CherryPickAsync([commit]), Loc.T("Cherry-picking…", "Cherry-pick yapılıyor…"),
            new ConfirmOperationDialogViewModel("CHERRY-PICK", Loc.T("Copy this commit here", "Bu commit'i buraya kopyala"),
                [Loc.T($"The changes of \"{commit.Subject}\" will be applied as a new commit on {BranchDisplay}.", $"\"{commit.Subject}\" değişiklikleri {BranchDisplay} üzerinde yeni bir commit olarak uygulanacak.")], $"git cherry-pick {commit.ShortSha}", IsPro,
                confirmText: "Cherry-pick", isDanger: false));

    public Task RevertAsync(CommitInfo commit) =>
        RunAsync(() => Session.Actions.RevertAsync(commit), Loc.T("Reverting…", "Revert yapılıyor…"),
            new ConfirmOperationDialogViewModel("REVERT", Loc.T("Create a commit that undoes this", "Bunu geri alan bir commit oluştur"),
                [Loc.T($"A new commit will reverse the changes of \"{commit.Subject}\". History is kept.", $"Yeni bir commit \"{commit.Subject}\" değişikliklerini tersine çevirecek. Geçmiş korunur.")], $"git revert {commit.ShortSha}", IsPro,
                confirmText: "Revert", isDanger: false));

    public async Task EditHistoryFromAsync(CommitInfo commit)
    {
        if (Head.BranchName is null)
        {
            Shell.ShowToast(new ToastViewModel(Loc.T("Switch to a branch to edit its history.", "Geçmişini düzenlemek için bir branch'e geçin."), ToastKind.Warning));
            return;
        }
        var baseRevision = commit.Parents.Count > 0 ? commit.Parents[0] : null;
        var commits = baseRevision is null
            ? (await Session.Git.History.GetCommitsAsync(new LogQuery { Revisions = ["HEAD"], MaxCount = 200 })).Reverse().ToList()
            : (await Session.Git.History.GetRangeAsync(baseRevision, "HEAD")).ToList();
        if (commits.Count == 0 || commits.All(c => c.Sha != commit.Sha))
        {
            Shell.ShowToast(new ToastViewModel(Loc.T("This commit is not part of the current branch.", "Bu commit mevcut branch'in parçası değil."), ToastKind.Warning));
            return;
        }
        if (commits.Any(c => c.IsMerge))
        {
            Shell.ShowToast(new ToastViewModel(Loc.T("Editing history across merge commits isn't supported yet.", "Merge commit'leri üzerinden geçmiş düzenleme henüz desteklenmiyor."), ToastKind.Warning));
            return;
        }
        if (await Shell.ShowDialogAsync(new InteractiveRebaseDialogViewModel(commits, Head.BranchName)) is not { } request) return;
        await RunAsync(() => Session.Actions.InteractiveRebaseAsync(baseRevision, request.Items), Loc.T("Rewriting history…", "Geçmiş yeniden yazılıyor…"), successMessage: Loc.T("History updated", "Geçmiş güncellendi"));
    }

    public async Task CreateTagAsync(CommitInfo commit)
    {
        var dialog = new TextInputDialogViewModel(Loc.T("Create tag", "Tag oluştur"), Loc.T($"Tag {commit.ShortSha} \"{commit.Subject}\".", $"{commit.ShortSha} \"{commit.Subject}\" commit'ine tag ekleyin."), Loc.T("Tag name", "Tag adı"), confirmText: Loc.T("Create", "Oluştur"),
            optionText: Loc.T("Annotated tag (with message)", "Açıklamalı tag (mesajlı)"), validate: name => Task.FromResult(string.IsNullOrWhiteSpace(name) || name.Contains(' ') ? Loc.T("Tag names can't contain spaces.", "Tag adları boşluk içeremez.") : null));
        if (await Shell.ShowDialogAsync(dialog) is not { } result) return;
        await RunAsync(() => Session.Actions.CreateTagAsync(result.Text, commit.Sha, result.Option ? result.Text : null), Loc.T("Creating tag…", "Tag oluşturuluyor…"));
    }

    public Task DeleteTagAsync(RefInfo tag) =>
        RunAsync(() => Session.Actions.DeleteTagAsync(tag), Loc.T("Deleting tag…", "Tag siliniyor…"),
            new ConfirmOperationDialogViewModel(Loc.T("DELETE TAG", "TAG SİL"), Loc.T("Delete this tag", "Bu tag'i sil"), [Loc.T($"The tag {tag.Name} will be removed locally.", $"{tag.Name} tag'i yerelde kaldırılacak.")], $"git tag -d {tag.Name}", IsPro, confirmText: Loc.T("Delete", "Sil")));

    // --- Çalışma alanı ---
    public Task<OperationOutcome?> CommitAsync(string message, bool amend)
    {
        ConfirmOperationDialogViewModel? confirm = null;
        if (amend)
        {
            confirm = new ConfirmOperationDialogViewModel("COMMIT --AMEND", Loc.T("Change the last commit", "Son commit'i değiştir"),
                [Loc.T("The last commit will be replaced by a new one with your staged changes and message.", "Son commit, stage edilmiş değişiklikleriniz ve mesajınızla yenisiyle değiştirilecek."), Loc.T("If it was already pushed, you will need to force push.", "Zaten push edildiyse force push yapmanız gerekecek.")],
                "git commit --amend", IsPro, confirmText: "Amend", isDanger: false);
        }
        return RunAsync(() => Session.Actions.CommitAsync(message, amend), amend ? Loc.T("Amending…", "Amend yapılıyor…") : Loc.T("Committing…", "Commit yapılıyor…"), confirm,
            successMessage: IsPro ? (amend ? Loc.T("Amended last commit", "Son commit değiştirildi") : Loc.T("Committed", "Commit edildi")) : Loc.T("Your work is saved in a commit", "Çalışmanız bir commit olarak kaydedildi"));
    }

    public Task DiscardAsync(IReadOnlyCollection<StatusEntry> entries)
    {
        var names = entries.Take(3).Select(e => e.Path).ToList();
        var confirm = new ConfirmOperationDialogViewModel(Loc.T("DISCARD CHANGES", "DEĞİŞİKLİKLERİ AT"), Loc.T("Throw away these changes", "Bu değişiklikleri at"),
        [
            entries.Count == 1 ? Loc.T($"Changes in {names[0]} will be thrown away.", $"{names[0]} dosyasındaki değişiklikler atılacak.") : Loc.T($"Changes in {entries.Count} files will be thrown away ({string.Join(", ", names)}{(entries.Count > 3 ? "…" : "")}).", $"{entries.Count} dosyadaki değişiklikler atılacak ({string.Join(", ", names)}{(entries.Count > 3 ? "…" : "")})."),
            entries.Any(e => e.IsUntracked) ? Loc.T("New files that were never committed will be deleted.", "Hiç commit edilmemiş yeni dosyalar silinecek.") : Loc.T("Files go back to their last saved version.", "Dosyalar son kaydedilen sürümlerine döner."),
        ], "git restore -- … / git clean -f -- …", IsPro, confirmText: Loc.T("Discard", "At"));
        return RunAsync(() => Session.Actions.DiscardAsync(entries, includeStaged: false), Loc.T("Discarding…", "Değişiklikler atılıyor…"), confirm);
    }

    [RelayCommand]
    public async Task Stash()
    {
        var dialog = new TextInputDialogViewModel(IsPro ? Loc.T("Stash changes", "Değişiklikleri stash'le") : Loc.T("Put my changes aside", "Değişikliklerimi kenara koy"),
            IsPro ? Loc.T("Save uncommitted changes to the stash and clean the working tree.", "Commit edilmemiş değişiklikleri stash'e kaydet ve çalışma alanını temizle.") : Loc.T("Your changes are saved and your files go back to the last commit. Bring them back any time.", "Değişiklikleriniz kaydedilir ve dosyalarınız son commit'e döner. İstediğiniz zaman geri getirebilirsiniz."),
            Loc.T("Description (optional)", "Açıklama (isteğe bağlı)"), confirmText: IsPro ? "Stash" : Loc.T("Put aside", "Kenara koy"), optionText: Loc.T("Include new (untracked) files", "Yeni (takip edilmeyen) dosyaları da dahil et"), optionDefault: true);
        if (await Shell.ShowDialogAsync(dialog) is not { } result) return;
        await RunAsync(() => Session.Actions.StashAsync(string.IsNullOrWhiteSpace(result.Text) ? null : result.Text, result.Option), Loc.T("Stashing…", "Stash yapılıyor…"));
    }

    public Task ApplyStashAsync(StashEntry stash, bool pop) =>
        RunAsync(() => Session.Actions.ApplyStashAsync(stash, pop), pop ? Loc.T("Restoring stash…", "Stash geri getiriliyor…") : Loc.T("Applying stash…", "Stash uygulanıyor…"));

    public Task DropStashAsync(StashEntry stash) =>
        RunAsync(() => Session.Actions.DropStashAsync(stash), Loc.T("Deleting stash…", "Stash siliniyor…"),
            new ConfirmOperationDialogViewModel("STASH DROP", Loc.T("Delete these saved changes", "Kaydedilmiş bu değişiklikleri sil"), [Loc.T($"The stash \"{stash.Message}\" will be deleted.", $"\"{stash.Message}\" stash'i silinecek.")], $"git stash drop {stash.Selector}", IsPro, confirmText: Loc.T("Delete", "Sil")));

    [RelayCommand]
    private Task Abort() => RunAsync(() => Session.Actions.AbortAsync(), Loc.T("Aborting…", "İptal ediliyor…"),
        new ConfirmOperationDialogViewModel(Loc.T("ABORT", "İPTAL (ABORT)"), Loc.T("Stop and go back", "Durdur ve geri dön"), [Loc.T($"The {StateTitle.ToLowerInvariant()} will be stopped and files return to how they were before it started.", $"Devam eden işlem ({StateTitle}) durdurulacak ve dosyalar başlamadan önceki hâline dönecek.")],
            "git " + (State == RepositoryState.Merging ? "merge" : State == RepositoryState.Rebasing ? "rebase" : "cherry-pick") + " --abort", IsPro, confirmText: Loc.T("Abort", "İptal et")));

    [RelayCommand]
    private Task Continue() => RunAsync(() => Session.Actions.ContinueAsync(), Loc.T("Continuing…", "Devam ediliyor…"));

    [RelayCommand]
    private async Task SaveSnapshot()
    {
        try
        {
            var result = await Task.Run(() => Session.TimeMachine.SaveSnapshotNowAsync());
            RefreshChrono();
            TimeMachine.MarkStale();
            Shell.ShowToast(new ToastViewModel(Loc.T("Snapshot saved", "Anlık görüntü kaydedildi"), ToastKind.Time,
                result.Snapshot.EntryCount == 0 ? Loc.T("No uncommitted changes — HEAD and staging area recorded.", "Commit edilmemiş değişiklik yok — HEAD ve staging alanı kaydedildi.") : Loc.T($"{result.Snapshot.EntryCount} uncommitted file(s) saved.", $"Commit edilmemiş {result.Snapshot.EntryCount} dosya kaydedildi.")));
        }
        catch (GitException ex)
        {
            await Shell.ShowErrorAsync(ex.Error);
        }
    }

    [RelayCommand]
    private void OpenTerminal() => ShellIntegration.OpenTerminal(RootPath, Shell.Services.Settings.Current.TerminalApplication);

    [RelayCommand]
    private void RevealInFileManager() => ShellIntegration.RevealInFileManager(RootPath);

    [RelayCommand]
    private void ToggleConsole() => IsConsoleOpen = !IsConsoleOpen;

    [RelayCommand]
    private void GoToWorkspace() => Section = RepositorySection.Workspace;

    [RelayCommand]
    private void GoToRecovery() => Section = RepositorySection.Recovery;

    [RelayCommand]
    private void GoToTimeMachine() => Section = RepositorySection.TimeMachine;

    [RelayCommand]
    private void ShowConflicts()
    {
        Section = RepositorySection.Workspace;
        Workspace.ShowConflicts();
    }

    [RelayCommand]
    private async Task Refresh() => await RefreshAsync(RepositoryChange.All);

    partial void OnSectionChanged(RepositorySection value)
    {
        if (value == RepositorySection.Recovery) _ = Recovery.EnsureLoadedAsync();
        if (value == RepositorySection.TimeMachine) _ = TimeMachine.EnsureLoadedAsync();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    public ObservableCollection<RefInfo> LocalBranchesForSwitcher => new(Refs.Where(r => r.Kind == RefKind.LocalBranch));
}
