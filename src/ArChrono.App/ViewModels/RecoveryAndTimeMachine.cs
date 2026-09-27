using System.Collections.ObjectModel;
using System.Text.Json;
using ArChrono.App.Controls;
using ArChrono.App.Infrastructure;
using ArChrono.App.ViewModels.Dialogs;
using ArChrono.Git.Diffing;
using ArChrono.Git.Errors;
using ArChrono.Git.Models;
using ArChrono.Localization;
using ArChrono.Recovery.Operations;
using ArChrono.Recovery.Restore;
using ArChrono.Recovery.Snapshots;
using ArChrono.Recovery.Timeline;
using ArChrono.Storage.Records;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArChrono.App.ViewModels;

public sealed class TimelineItemViewModel
{
    private TimelineItemViewModel(string? header, TimelineEntry? entry)
    {
        Header = header;
        Entry = entry;
    }

    public static TimelineItemViewModel ForHeader(string header) => new(header, null);

    public static TimelineItemViewModel ForEntry(TimelineEntry entry) => new(null, entry);

    public string? Header { get; }
    public TimelineEntry? Entry { get; }
    public bool IsHeader => Header is not null;
    public bool IsEntry => Entry is not null;
    public string Time => Entry?.Time.ToString("HH:mm") ?? "";
    public string Title => Entry?.Title ?? "";
    public string Detail => Entry?.Detail ?? "";
    public bool HasDetail => !string.IsNullOrEmpty(Entry?.Detail);
    public string Branch => Entry?.Branch ?? "";
    public bool HasBranch => !string.IsNullOrEmpty(Entry?.Branch);

    public string IconKey => Entry?.Kind switch
    {
        TimelineEntryKind.RecoveryPoint => Entry.Operation?.Status == OperationStatus.Failed ? "Icon.Alert" : "Icon.Recovery",
        TimelineEntryKind.Snapshot => "Icon.Snapshot",
        TimelineEntryKind.Reflog => "Icon.Reflog",
        _ => "Icon.Lost",
    };

    public string AccentKey => Entry?.Kind switch
    {
        TimelineEntryKind.RecoveryPoint => Entry.Operation?.Status == OperationStatus.Failed ? "Chrono.Danger" : "Chrono.Time",
        TimelineEntryKind.Snapshot => "Chrono.Safe",
        TimelineEntryKind.Reflog => "Chrono.TextTertiary",
        _ => "Chrono.Ai",
    };
}

public sealed record RefLine(string Name, string Sha);

/// <summary>Recovery Center: recovery point'ler + Time Machine snapshot'ları + reflog + kayıp commit'ler tek zaman çizelgesinde.</summary>
public sealed partial class RecoveryCenterViewModel : ViewModelBase
{
    private readonly RepositoryViewModel _owner;
    private bool _stale = true;
    private IReadOnlyList<TimelineEntry> _entries = [];
    private IReadOnlyList<TimelineEntry> _lost = [];

    public RecoveryCenterViewModel(RepositoryViewModel owner)
    {
        _owner = owner;
    }

    public ObservableCollection<TimelineItemViewModel> Items { get; } = [];
    public ObservableCollection<SnapshotEntryRecord> ChangedFiles { get; } = [];
    public bool HasChangedFiles => ChangedFiles.Count > 0;
    public ObservableCollection<RefLine> RefLines { get; } = [];

    [ObservableProperty] public partial bool ShowPoints { get; set; } = true;
    [ObservableProperty] public partial bool ShowSnapshots { get; set; } = true;
    [ObservableProperty] public partial bool ShowReflog { get; set; } = true;
    [ObservableProperty] public partial string Search { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsScanning { get; set; }
    [ObservableProperty] public partial string ScanSummary { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedTitle), nameof(SelectedTime), nameof(SelectedBranch), nameof(SelectedHead),
        nameof(SelectedOperation), nameof(SelectedKindText), nameof(CanRestore), nameof(CanUndoOperation), nameof(CanCreateBranch),
        nameof(IsLostStash), nameof(IsPinned), nameof(CanPin), nameof(SnapshotInfo), nameof(HasSnapshot), nameof(SelectedCommand), nameof(HasCommand))]
    public partial TimelineItemViewModel? Selected { get; set; }

    public bool HasSelection => Selected?.Entry is not null;
    private TimelineEntry? Entry => Selected?.Entry;
    public string SelectedTitle => Entry?.Title ?? "";
    public string SelectedTime => Entry is null ? "" : Format.Date(Entry.Time, "dddd, MMM d · HH:mm:ss", "d MMMM dddd · HH:mm:ss");
    public string SelectedBranch => Entry?.Branch ?? (Entry?.Kind is TimelineEntryKind.LostCommit or TimelineEntryKind.LostStash ? Loc.T("not on any branch", "hiçbir branch'te değil") : "—");
    public string SelectedHead => Entry?.Sha is { } sha ? sha[..Math.Min(12, sha.Length)] : "—";
    public string SelectedOperation => Entry?.Operation is { } op ? $"{op.Title} — {RecoveryTimelineService.DescribeOutcome(op)}" : Entry?.Detail ?? "";
    public string SelectedCommand => Entry?.Operation?.CommandText ?? Entry?.Reflog?.Message ?? "";
    public bool HasCommand => _owner.IsPro && SelectedCommand.Length > 0;
    public string SelectedKindText => Entry?.Kind switch
    {
        TimelineEntryKind.RecoveryPoint => Entry.Point!.Kind switch
        {
            RecoveryPointKind.Manual => Loc.T("Manual recovery point", "Elle kaydedilen kurtarma noktası"),
            RecoveryPointKind.BeforeRestore => Loc.T("Saved automatically before an undo/restore", "Geri alma/geri yükleme öncesi otomatik kaydedildi"),
            _ => Loc.T("Saved automatically before an operation", "Bir işlem öncesi otomatik kaydedildi"),
        },
        TimelineEntryKind.Snapshot => Loc.T("Time Machine snapshot of your working tree", "Çalışma alanınızın Zaman Makinesi anlık görüntüsü"),
        TimelineEntryKind.Reflog => Loc.T("Git reflog entry (something changed HEAD outside ArChrono)", "Git reflog kaydı (HEAD, ArChrono dışında değişti)"),
        TimelineEntryKind.LostStash => Loc.T("Stash that is no longer in the stash list", "Artık stash listesinde olmayan stash"),
        TimelineEntryKind.LostCommit => Loc.T("Commit that is not on any branch", "Hiçbir branch'te olmayan commit"),
        _ => "",
    };

    public bool CanRestore => Entry?.Kind == TimelineEntryKind.RecoveryPoint;
    public bool HasSnapshot => Entry?.Kind == TimelineEntryKind.Snapshot || Entry?.Point?.SnapshotId is not null;
    public bool CanUndoOperation => Entry?.Operation is { BeforePointId: not null, UndoneByOperationId: null } op && op.Kind is not ("undo" or "redo");
    public bool CanCreateBranch => Entry?.Kind is TimelineEntryKind.Reflog or TimelineEntryKind.LostCommit or TimelineEntryKind.LostStash && Entry.Sha is not null;
    public bool IsLostStash => Entry?.Kind == TimelineEntryKind.LostStash;
    public bool CanPin => Entry?.Point is not null;
    public bool IsPinned => Entry?.Point?.IsPinned == true;

    [ObservableProperty]
    public partial string SnapshotInfo { get; set; } = string.Empty;

    public void MarkStale() => _stale = true;

    public async Task EnsureLoadedAsync()
    {
        if (_stale) await ReloadAsync();
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        _stale = false;
        IsLoading = true;
        try
        {
            var session = _owner.Session;
            _entries = await Task.Run(() => _owner.Shell.Services.Recovery.Timeline.GetTimelineAsync(session.Git, session.Record.Id));
            Rebuild();
        }
        catch (GitException ex)
        {
            _owner.Shell.ShowToast(new ToastViewModel(Loc.T("Could not load the timeline.", "Zaman çizelgesi yüklenemedi."), ToastKind.Warning, ex.Error.Title));
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnShowPointsChanged(bool value) => Rebuild();
    partial void OnShowSnapshotsChanged(bool value) => Rebuild();
    partial void OnShowReflogChanged(bool value) => Rebuild();
    partial void OnSearchChanged(string value) => Rebuild();

    private void Rebuild()
    {
        var selectedId = Selected?.Entry?.Id;
        Items.Clear();
        var filtered = _entries.Concat(_lost)
            .Where(e => e.Kind switch
            {
                TimelineEntryKind.RecoveryPoint => ShowPoints,
                TimelineEntryKind.Snapshot => ShowSnapshots,
                TimelineEntryKind.Reflog => ShowReflog,
                _ => true,
            })
            .Where(e => Search.Length == 0 || e.Title.Contains(Search, StringComparison.OrdinalIgnoreCase) || (e.Branch?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderByDescending(e => e.Time);

        string? day = null;
        foreach (var entry in filtered)
        {
            var header = Format.DayHeader(entry.Time);
            if (header != day)
            {
                Items.Add(TimelineItemViewModel.ForHeader(header));
                day = header;
            }
            Items.Add(TimelineItemViewModel.ForEntry(entry));
        }
        Selected = Items.FirstOrDefault(i => i.Entry?.Id == selectedId) ?? Items.FirstOrDefault(i => i.IsEntry);
    }

    public async Task SelectPointAsync(long pointId)
    {
        await ReloadAsync();
        ShowPoints = true;
        Selected = Items.FirstOrDefault(i => i.Entry?.Point?.Id == pointId) ?? Selected;
    }

    partial void OnSelectedChanged(TimelineItemViewModel? value)
    {
        if (value is { IsHeader: true })
        {
            var index = Items.IndexOf(value);
            Selected = index + 1 < Items.Count ? Items[index + 1] : null;
            return;
        }
        ChangedFiles.Clear();
        RefLines.Clear();
        SnapshotInfo = string.Empty;
        if (value?.Entry is not { } entry) return;

        var storage = _owner.Shell.Services.Storage;
        var snapshotId = entry.Snapshot?.Id ?? entry.Point?.SnapshotId;
        if (snapshotId is { } id && storage.Snapshots.Get(id) is { } snapshot)
        {
            foreach (var file in storage.Snapshots.GetEntries(id)) ChangedFiles.Add(file);
            OnPropertyChanged(nameof(HasChangedFiles));
            var skipped = snapshot.SkippedJson is null ? 0 : JsonSerializer.Deserialize<List<SkippedFile>>(snapshot.SkippedJson)?.Count ?? 0;
            SnapshotInfo = Loc.T($"Snapshot #{snapshot.Id} · {snapshot.EntryCount} uncommitted file(s) · {Format.Bytes(snapshot.TotalBytes)} of content · ", $"Anlık görüntü #{snapshot.Id} · commit edilmemiş {snapshot.EntryCount} dosya · {Format.Bytes(snapshot.TotalBytes)} içerik · ") +
                           Loc.T($"{Format.Bytes(snapshot.NewBytes)} newly stored", $"{Format.Bytes(snapshot.NewBytes)} yeni saklandı") + (skipped > 0 ? Loc.T($" · {skipped} skipped (too large)", $" · {skipped} atlandı (çok büyük)") : "") +
                           (_owner.IsPro ? $"\nindex tree {snapshot.IndexTree[..10]} · pin {snapshot.PinCommit?[..10] ?? Loc.T("none", "yok")}" : "");
        }
        if (entry.Point is { } point)
        {
            foreach (var r in storage.RecoveryPoints.GetRefs(point.Id).Where(r => !r.RefName.StartsWith("refs/remotes/", StringComparison.Ordinal)))
                RefLines.Add(new RefLine(RefNames.Shorten(r.RefName), r.TargetSha[..7]));
        }
    }

    [RelayCommand]
    private Task RestoreEverything() => Entry?.Point is { } point ? _owner.RestoreToPointAsync(point, RestoreMode.Everything) : Task.CompletedTask;

    [RelayCommand]
    private async Task RestoreFiles()
    {
        if (Entry?.Point is { } point) await _owner.RestoreToPointAsync(point, RestoreMode.FilesOnly);
        else if (Entry?.Snapshot is { } snapshot) await _owner.TimeMachine.RestoreWholeSnapshotAsync(snapshot);
    }

    [RelayCommand]
    private Task UndoOperation() => Entry?.Operation is { } op ? _owner.UndoOperationAsync(op, alwaysPreview: true) : Task.CompletedTask;

    [RelayCommand]
    private async Task OpenInTimeMachine()
    {
        var storage = _owner.Shell.Services.Storage;
        var snapshot = Entry?.Snapshot ?? (Entry?.Point?.SnapshotId is { } id ? storage.Snapshots.Get(id) : null);
        if (snapshot is null) return;
        _owner.Section = RepositorySection.TimeMachine;
        await _owner.TimeMachine.SelectSnapshotAsync(snapshot);
    }

    [RelayCommand]
    private void TogglePin()
    {
        if (Entry?.Point is not { } point) return;
        _owner.Shell.Services.Storage.RecoveryPoints.SetPinned(point.Id, !point.IsPinned);
        _owner.Shell.ShowToast(new ToastViewModel(point.IsPinned ? Loc.T("Recovery point unpinned", "Kurtarma noktasının sabitlemesi kaldırıldı") : Loc.T("Pinned — cleanup will never remove it", "Sabitlendi — temizlik onu asla silmez"), ToastKind.Time));
        _ = ReloadAsync();
    }

    [RelayCommand]
    private Task CreateBranchHere() => Entry?.Sha is { } sha ? _owner.CreateBranch(sha) : Task.CompletedTask;

    [RelayCommand]
    private async Task RestoreStash()
    {
        if (Entry?.Commit is not { } commit) return;
        await _owner.RunAsync(() => _owner.Session.Actions.RunConsoleCommandAsync(["stash", "store", "-m", commit.Subject, commit.Sha]), Loc.T("Restoring stash…", "Stash geri getiriliyor…"),
            successMessage: Loc.T("Stash restored", "Stash geri getirildi"));
        _lost = _lost.Where(l => l.Sha != commit.Sha).ToList();
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task SaveRecoveryPoint()
    {
        var dialog = new TextInputDialogViewModel(Loc.T("Save a recovery point", "Kurtarma noktası kaydet"), Loc.T("Record the exact state of branches, HEAD, staging area and files right now.", "Branch'lerin, HEAD'in, staging alanının ve dosyaların şu anki durumunu olduğu gibi kaydedin."),
            Loc.T("Name", "Ad"), Loc.T("Manual save ", "Elle kayıt ") + DateTimeOffset.Now.ToString("HH:mm"), Loc.T("Save", "Kaydet"));
        if (await _owner.Shell.ShowDialogAsync(dialog) is not { } result) return;
        try
        {
            var point = await Task.Run(() => _owner.Session.CreateManualRecoveryPointAsync(result.Text));
            _owner.Shell.ShowToast(new ToastViewModel(Loc.T("Recovery point saved", "Kurtarma noktası kaydedildi"), ToastKind.Time, point.Title));
            _owner.RefreshChrono();
            await SelectPointAsync(point.Id);
        }
        catch (GitException ex)
        {
            await _owner.Shell.ShowErrorAsync(ex.Error);
        }
    }

    [RelayCommand]
    private async Task DeepScan()
    {
        IsScanning = true;
        ScanSummary = Loc.T("Scanning the object database…", "Nesne veritabanı taranıyor…");
        try
        {
            var session = _owner.Session;
            _lost = await Task.Run(() => _owner.Shell.Services.Recovery.Timeline.FindLostWorkAsync(session.Git));
            ScanSummary = _lost.Count == 0 ? Loc.T("No lost commits or stashes found.", "Kayıp commit veya stash bulunamadı.") : Loc.T($"Found {_lost.Count} lost item(s). They are listed in the timeline.", $"{_lost.Count} kayıp öğe bulundu. Zaman çizelgesinde listelendiler.");
            Rebuild();
        }
        catch (GitException ex)
        {
            ScanSummary = ex.Error.Title;
        }
        finally
        {
            IsScanning = false;
        }
    }
}

public sealed record SnapshotItem(SnapshotRecord Snapshot)
{
    public string Time => Snapshot.CreatedAt.ToString("HH:mm:ss");
    public string Title => Snapshot.Label ?? Snapshot.Trigger switch
    {
        SnapshotTrigger.Timer => Loc.T("Automatic snapshot", "Otomatik anlık görüntü"),
        SnapshotTrigger.Operation => Loc.T("Before/after operation", "İşlem öncesi/sonrası"),
        SnapshotTrigger.Shutdown => Loc.T("App closed", "Uygulama kapandı"),
        _ => Loc.T("Snapshot", "Anlık görüntü"),
    };
    public string Meta => (Snapshot.HeadRef is null ? Loc.T("detached", "branch dışında") : RefNames.Shorten(Snapshot.HeadRef)) + Loc.T($" · {Snapshot.EntryCount} uncommitted file(s)", $" · commit edilmemiş {Snapshot.EntryCount} dosya");
    public string AccentKey => Snapshot.Trigger == SnapshotTrigger.Operation ? "Chrono.Time" : "Chrono.Safe";
}

public sealed record SnapshotChangeItem(SnapshotFileChange Change)
{
    public string Path => Change.Path;
    public string FileName => System.IO.Path.GetFileName(Change.Path);
    public string Directory => System.IO.Path.GetDirectoryName(Change.Path)?.Replace('\\', '/') ?? "";
    public string Letter => Format.ChangeLetter(Change.Change);
    public string BrushKey => Format.ChangeResource(Change.Change);
}

public sealed record FileVersionItem(SnapshotRecord Snapshot, string? BlobId)
{
    public string Time => Format.Date(Snapshot.CreatedAt, "MMM d · HH:mm:ss", "d MMM · HH:mm:ss");
    public string State => BlobId is null ? Loc.T("file did not exist", "dosya yoktu") : BlobId[..8];
}

/// <summary>Code Time Machine: günlük snapshot ekseni, snapshot ↔ snapshot/current karşılaştırma, dosya zaman makinesi, geri yükleme.</summary>
public sealed partial class TimeMachineViewModel : ViewModelBase
{
    private readonly RepositoryViewModel _owner;
    private bool _stale = true;

    public TimeMachineViewModel(RepositoryViewModel owner)
    {
        _owner = owner;
    }

    public ObservableCollection<SnapshotItem> Snapshots { get; } = [];
    public ObservableCollection<SnapshotChangeItem> Changes { get; } = [];
    public bool HasSnapshots => Snapshots.Count > 0;
    public bool ShowEmptyDiff => Diff is null && SelectedVersion is null;
    public ObservableCollection<FileVersionItem> FileVersions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DayTitle), nameof(CanGoNext))]
    public partial DateTime Day { get; set; } = DateTime.Today;

    [ObservableProperty] public partial IReadOnlyList<ChronoMark> Marks { get; set; } = [];
    [ObservableProperty] public partial DateTimeOffset RangeStart { get; set; }
    [ObservableProperty] public partial DateTimeOffset RangeEnd { get; set; }
    [ObservableProperty] public partial SnapshotItem? Selected { get; set; }
    [ObservableProperty] public partial SnapshotChangeItem? SelectedChange { get; set; }
    [ObservableProperty] public partial DiffViewModel? Diff { get; set; }
    [ObservableProperty] public partial bool CompareWithCurrent { get; set; } = true;
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string CompareTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string FileQuery { get; set; } = string.Empty;
    [ObservableProperty] public partial FileVersionItem? SelectedVersion { get; set; }
    [ObservableProperty] public partial string VersionContent { get; set; } = string.Empty;
    [ObservableProperty] public partial string StorageText { get; set; } = string.Empty;

    public string DayTitle => Format.DayHeader(new DateTimeOffset(Day));
    public bool CanGoNext => Day < DateTime.Today;
    public bool CompareWithPrevious
    {
        get => !CompareWithCurrent;
        set => CompareWithCurrent = !value;
    }
    public bool HasSelection => Selected is not null;
    public object? SelectedPayload => Selected?.Snapshot;

    public void MarkStale() => _stale = true;

    public async Task EnsureLoadedAsync()
    {
        if (_stale) await LoadDayAsync();
    }

    partial void OnDayChanged(DateTime value) => _ = LoadDayAsync();

    partial void OnCompareWithCurrentChanged(bool value)
    {
        OnPropertyChanged(nameof(CompareWithPrevious));
        _ = LoadChangesAsync();
    }

    partial void OnSelectedChanged(SnapshotItem? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedPayload));
        _ = LoadChangesAsync();
    }

    partial void OnSelectedChangeChanged(SnapshotChangeItem? value) => _ = LoadDiffAsync(value);

    partial void OnDiffChanged(DiffViewModel? value) => OnPropertyChanged(nameof(ShowEmptyDiff));

    [RelayCommand]
    private void PreviousDay() => Day = Day.AddDays(-1);

    [RelayCommand]
    private void NextDay()
    {
        if (CanGoNext) Day = Day.AddDays(1);
    }

    [RelayCommand]
    public async Task LoadDayAsync()
    {
        _stale = false;
        var start = new DateTimeOffset(Day);
        var end = start.AddDays(1);
        var list = _owner.Session.TimeMachine.GetSnapshots(start, end);
        var selectedId = Selected?.Snapshot.Id;
        Snapshots.Clear();
        foreach (var snapshot in list) Snapshots.Add(new SnapshotItem(snapshot));
        OnPropertyChanged(nameof(HasSnapshots));

        RangeStart = list.Count == 0 ? start.AddHours(8) : list.Min(s => s.CreatedAt).AddMinutes(-20);
        RangeEnd = Day == DateTime.Today ? DateTimeOffset.Now.AddMinutes(10) : list.Count == 0 ? start.AddHours(18) : list.Max(s => s.CreatedAt).AddMinutes(20);
        if (RangeEnd - RangeStart < TimeSpan.FromHours(1)) RangeStart = RangeEnd.AddHours(-1);
        Marks = list.Select(s => new ChronoMark(s.CreatedAt, s.Trigger == SnapshotTrigger.Operation ? ChronoMarkKind.RecoveryPoint : ChronoMarkKind.Snapshot,
            new SnapshotItem(s).Title, s)).ToList();

        var used = _owner.Shell.Services.Storage.Blobs.GetTotalStoredBytes();
        var settings = _owner.Shell.Services.Settings.Current.TimeMachine;
        StorageText = $"{(settings.Enabled ? Loc.T($"Snapshot every {settings.IntervalMinutes} min when files change", $"Dosyalar değiştikçe {settings.IntervalMinutes} dakikada bir anlık görüntü") : Loc.T("Automatic snapshots are off", "Otomatik anlık görüntüler kapalı"))} · " +
                      Loc.T($"{Format.Bytes(used)} of {Format.Bytes((long)(settings.MaxStorageGigabytes * 1024 * 1024 * 1024))} used", $"{Format.Bytes(used)} / {Format.Bytes((long)(settings.MaxStorageGigabytes * 1024 * 1024 * 1024))} kullanıldı");

        Selected = Snapshots.FirstOrDefault(s => s.Snapshot.Id == selectedId) ?? Snapshots.FirstOrDefault();
        await Task.CompletedTask;
    }

    public async Task SelectSnapshotAsync(SnapshotRecord snapshot)
    {
        var day = snapshot.CreatedAt.LocalDateTime.Date;
        if (Day != day) Day = day;
        await LoadDayAsync();
        Selected = Snapshots.FirstOrDefault(s => s.Snapshot.Id == snapshot.Id);
    }

    public void OnMarkClicked(ChronoMark mark)
    {
        if (mark.Payload is SnapshotRecord snapshot) Selected = Snapshots.FirstOrDefault(s => s.Snapshot.Id == snapshot.Id);
    }

    private async Task LoadChangesAsync()
    {
        Changes.Clear();
        Diff = null;
        if (Selected is null) return;
        var snapshot = Selected.Snapshot;
        IsLoading = true;
        try
        {
            IReadOnlyList<SnapshotFileChange> changes;
            if (CompareWithCurrent)
            {
                CompareTitle = Loc.T($"Changes from {snapshot.CreatedAt:HH:mm:ss} to now", $"{snapshot.CreatedAt:HH:mm:ss} anından şimdiye değişiklikler");
                changes = await Task.Run(() => _owner.Session.TimeMachine.CompareAsync(snapshot, null));
            }
            else
            {
                var previous = _owner.Shell.Services.Storage.Snapshots.List(_owner.Session.Record.Id, null, snapshot.CreatedAt.AddMilliseconds(-1), 1).FirstOrDefault();
                if (previous is null)
                {
                    CompareTitle = Loc.T("This is the earliest snapshot.", "Bu en eski anlık görüntü.");
                    return;
                }
                CompareTitle = Loc.T($"Changes from {previous.CreatedAt:HH:mm:ss} to {snapshot.CreatedAt:HH:mm:ss}", $"{previous.CreatedAt:HH:mm:ss} → {snapshot.CreatedAt:HH:mm:ss} arasındaki değişiklikler");
                changes = await Task.Run(() => _owner.Session.TimeMachine.CompareAsync(previous, snapshot));
            }
            if (Selected?.Snapshot.Id != snapshot.Id) return;
            foreach (var change in changes) Changes.Add(new SnapshotChangeItem(change));
            if (changes.Count == 0) CompareTitle += Loc.T(" — identical", " — aynı");
            SelectedChange = Changes.FirstOrDefault();
        }
        catch (GitException ex)
        {
            CompareTitle = ex.Error.Title;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadDiffAsync(SnapshotChangeItem? item)
    {
        if (item is null)
        {
            Diff = null;
            return;
        }
        var diff = await Task.Run(() => _owner.Session.TimeMachine.DiffAsync(item.Change));
        if (SelectedChange == item) Diff = new DiffViewModel(diff);
    }

    [RelayCommand]
    private async Task RestoreSelectedFile()
    {
        if (Selected is null || SelectedChange is null) return;
        var snapshot = CompareWithCurrent ? Selected.Snapshot
            : _owner.Shell.Services.Storage.Snapshots.List(_owner.Session.Record.Id, null, Selected.Snapshot.CreatedAt.AddMilliseconds(-1), 1).FirstOrDefault();
        if (snapshot is null) return;
        var path = SelectedChange.Path;
        var confirm = new ConfirmOperationDialogViewModel(Loc.T("RESTORE FILE", "DOSYAYI GERİ YÜKLE"), Loc.T("Bring back this version", "Bu sürümü geri getir"),
            [Loc.T($"{path} will be restored as it was at {snapshot.CreatedAt:HH:mm:ss}.", $"{path}, {snapshot.CreatedAt:HH:mm:ss} anındaki hâline getirilecek."), Loc.T("Your current version is saved in a recovery point first.", "Mevcut sürümünüz önce bir kurtarma noktasına kaydedilir.")],
            $"archrono restore-snapshot #{snapshot.Id} -- {path}", _owner.IsPro, confirmText: Loc.T("Restore", "Geri yükle"), isDanger: false);
        await _owner.RunAsync(() => _owner.Session.TimeMachine.RestoreFilesAsync(snapshot, [path]), Loc.T("Restoring file…", "Dosya geri yükleniyor…"), confirm,
            successMessage: Loc.T($"Restored {System.IO.Path.GetFileName(path)} from {snapshot.CreatedAt:HH:mm}", $"{System.IO.Path.GetFileName(path)}, {snapshot.CreatedAt:HH:mm} anından geri yüklendi"));
        MarkStale();
        await LoadChangesAsync();
    }

    [RelayCommand]
    private Task RestoreAll() => Selected is null ? Task.CompletedTask : RestoreWholeSnapshotAsync(Selected.Snapshot);

    public async Task RestoreWholeSnapshotAsync(SnapshotRecord snapshot)
    {
        var confirm = new ConfirmOperationDialogViewModel(Loc.T("RESTORE WORKING TREE", "ÇALIŞMA ALANINI GERİ YÜKLE"), Loc.T("Restore all files from this moment", "Tüm dosyaları bu andan geri yükle"),
            [Loc.T($"Your files and staging area will be restored as they were at {snapshot.CreatedAt:HH:mm:ss}.", $"Dosyalarınız ve staging alanı {snapshot.CreatedAt:HH:mm:ss} anındaki hâline getirilecek."), Loc.T("Branches are not moved. Your current files are saved in a recovery point first.", "Branch'ler taşınmaz. Mevcut dosyalarınız önce bir kurtarma noktasına kaydedilir.")],
            $"archrono restore-snapshot #{snapshot.Id}", _owner.IsPro, confirmText: Loc.T("Restore all files", "Tüm dosyaları geri yükle"));
        await _owner.RunAsync(() => _owner.Session.TimeMachine.RestoreWorkingTreeAsync(snapshot), Loc.T("Restoring files…", "Dosyalar geri yükleniyor…"), confirm,
            successMessage: Loc.T($"Files restored from {snapshot.CreatedAt:HH:mm}", $"Dosyalar {snapshot.CreatedAt:HH:mm} anından geri yüklendi"));
        MarkStale();
    }

    [RelayCommand]
    private async Task SaveSnapshot()
    {
        await _owner.SaveSnapshotCommand.ExecuteAsync(null);
        await LoadDayAsync();
    }

    [RelayCommand]
    private async Task LookupFile()
    {
        FileVersions.Clear();
        VersionContent = string.Empty;
        var path = FileQuery.Trim().Replace('\\', '/').TrimStart('/');
        if (path.Length == 0) return;
        var timeline = await Task.Run(() => _owner.Session.TimeMachine.GetFileTimelineAsync(path));
        foreach (var (snapshot, blob) in timeline) FileVersions.Add(new FileVersionItem(snapshot, blob));
        if (FileVersions.Count == 0) VersionContent = Loc.T("No snapshots contain this path yet.", "Henüz bu yolu içeren anlık görüntü yok.");
        SelectedVersion = FileVersions.FirstOrDefault();
    }

    partial void OnSelectedVersionChanged(FileVersionItem? value)
    {
        OnPropertyChanged(nameof(ShowEmptyDiff));
        if (value is null) return;
        _ = Task.Run(async () =>
        {
            var content = await _owner.Session.TimeMachine.ReadFileAsync(value.Snapshot, FileQuery.Trim().Replace('\\', '/'));
            var text = content is null ? Loc.T("(the file did not exist at this moment)", "(dosya bu anda mevcut değildi)") : TextDiff.LooksBinary(content) ? Loc.T("(binary file)", "(ikili dosya)") : TextDiff.Decode(content);
            OnUi(() => VersionContent = text.Length > 200_000 ? text[..200_000] + "\n…" : text);
        });
    }

    [RelayCommand]
    private async Task RestoreVersion()
    {
        if (SelectedVersion is null) return;
        var path = FileQuery.Trim().Replace('\\', '/');
        var snapshot = SelectedVersion.Snapshot;
        await _owner.RunAsync(() => _owner.Session.TimeMachine.RestoreFilesAsync(snapshot, [path]), Loc.T("Restoring file…", "Dosya geri yükleniyor…"),
            new ConfirmOperationDialogViewModel(Loc.T("RESTORE FILE", "DOSYAYI GERİ YÜKLE"), Loc.T("Bring back this version", "Bu sürümü geri getir"), [Loc.T($"{path} will be restored as it was at {snapshot.CreatedAt:HH:mm:ss}.", $"{path}, {snapshot.CreatedAt:HH:mm:ss} anındaki hâline getirilecek.")],
                $"archrono restore-snapshot #{snapshot.Id} -- {path}", _owner.IsPro, confirmText: Loc.T("Restore", "Geri yükle"), isDanger: false));
    }
}
