using System.Collections.ObjectModel;
using ArChrono.App.Controls;
using ArChrono.App.Infrastructure;
using ArChrono.App.ViewModels.Dialogs;
using ArChrono.Git.Errors;
using ArChrono.Git.Graph;
using ArChrono.Git.Models;
using ArChrono.Git.Services;
using ArChrono.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArChrono.App.ViewModels;

public sealed record RefBadge(string Name, RefKind Kind, bool IsCurrent)
{
    public string Classes => Kind switch
    {
        RefKind.Tag => "time",
        RefKind.RemoteBranch => "",
        _ => "safe",
    };

    public string IconKey => Kind == RefKind.Tag ? "Icon.Tag" : "Icon.Branch";
    public string BrushKey => Kind switch
    {
        RefKind.Tag => "Chrono.Time",
        RefKind.RemoteBranch => "Chrono.TextSecondary",
        _ => "Chrono.Safe",
    };
    public string BackgroundKey => Kind switch
    {
        RefKind.Tag => "Chrono.TimeMuted",
        RefKind.RemoteBranch => "Chrono.Hover",
        _ => "Chrono.SafeMuted",
    };
    public string Display => IsCurrent ? "● " + Name : Name;
    public bool IsTag => Kind == RefKind.Tag;
    public bool IsRemote => Kind == RefKind.RemoteBranch;
}

public sealed partial class CommitRowViewModel(RepositoryViewModel owner, CommitInfo? commit, GraphRow graph, IReadOnlyList<RefBadge> badges, bool isHead) : ObservableObject
{
    public RepositoryViewModel Owner { get; } = owner;
    public CommitInfo? Commit { get; } = commit;
    public GraphRow Graph { get; } = graph;
    public IReadOnlyList<RefBadge> Badges { get; } = badges;
    public bool IsHead { get; } = isHead;
    public bool IsWorkingTree => Commit is null;
    public bool IsCommit => Commit is not null;
    public bool HasBadges => Badges.Count > 0;

    [ObservableProperty]
    public partial string WorkingTreeSummary { get; set; } = string.Empty;

    public string Subject => Commit?.Subject ?? WorkingTreeSummary;
    public string Author => Commit?.AuthorName ?? string.Empty;
    public string Date => Commit is null ? Loc.T("now", "şimdi") : Format.Relative(Commit.CommitDate);
    public string ShortSha => Commit?.ShortSha ?? string.Empty;

    public void NotifySubject() => OnPropertyChanged(nameof(Subject));

    [RelayCommand] private Task Checkout() => Commit is null ? Task.CompletedTask : Owner.CheckoutCommitAsync(Commit);
    [RelayCommand] private Task CreateBranchHere() => Commit is null ? Task.CompletedTask : Owner.CreateBranch(Commit.Sha);
    [RelayCommand] private Task CreateTag() => Commit is null ? Task.CompletedTask : Owner.CreateTagAsync(Commit);
    [RelayCommand] private Task CherryPick() => Commit is null ? Task.CompletedTask : Owner.CherryPickAsync(Commit);
    [RelayCommand] private Task Revert() => Commit is null ? Task.CompletedTask : Owner.RevertAsync(Commit);
    [RelayCommand] private Task ResetHere() => Commit is null ? Task.CompletedTask : Owner.ResetToAsync(Commit);
    [RelayCommand] private Task EditHistory() => Commit is null ? Task.CompletedTask : Owner.EditHistoryFromAsync(Commit);
    [RelayCommand] private Task CopySha() => Commit is null ? Task.CompletedTask : Owner.Shell.CopyToClipboardAsync(Commit.Sha);

    [RelayCommand]
    private void Explain()
    {
        Owner.Workspace.SelectedRow = this;
        if (Owner.Workspace.Details is CommitDetailsViewModel details) details.ShowExplanation();
    }
}

public sealed partial class BranchItemViewModel(RepositoryViewModel owner, RefInfo reference, bool isGuided) : ObservableObject
{
    public RepositoryViewModel Owner { get; } = owner;
    public RefInfo Ref { get; } = reference;
    public string Name => Ref.Kind == RefKind.RemoteBranch ? Ref.Name : Ref.Name;
    public bool IsCurrent => Ref.IsHead;
    public bool IsLocal => Ref.Kind == RefKind.LocalBranch;
    public bool IsTag => Ref.Kind == RefKind.Tag;
    public string IconKey => IsTag ? "Icon.Tag" : "Icon.Branch";
    public string LastActivity => Ref.LastCommitDate is { } d ? Format.Relative(d) : "";

    public string Tracking => Ref.UpstreamGone ? (isGuided ? Loc.T("removed on server", "sunucuda silindi") : Loc.T("upstream gone", "upstream yok"))
        : Ref.Upstream is null ? ""
        : Ref.Ahead == 0 && Ref.Behind == 0 ? ""
        : isGuided ? Loc.T($"{Ref.Ahead} to send · {Ref.Behind} to get", $"{Ref.Ahead} gönderilecek · {Ref.Behind} alınacak") : $"↑{Ref.Ahead} ↓{Ref.Behind}";

    public bool HasTracking => Tracking.Length > 0;
    public bool IsStale => Ref.LastCommitDate is { } d && DateTimeOffset.Now - d > TimeSpan.FromDays(60);

    public string Status => Ref.UpstreamGone ? Loc.T("Remote branch deleted", "Uzak branch silinmiş")
        : Ref.Behind > 0 && Ref.Ahead > 0 ? Loc.T("Needs rebase or merge", "Rebase veya merge gerekli")
        : Ref.Behind > 0 ? Loc.T("Behind the server", "Sunucunun gerisinde")
        : Ref.Ahead > 0 ? Loc.T("Not pushed yet", "Henüz push edilmedi")
        : IsStale ? Loc.T("Stale (no activity for 60+ days)", "Eski (60+ gündür hareketsiz)") : "";

    public string Tooltip => Loc.T($"{Ref.Name}\nLast activity: {LastActivity}\n{Ref.Subject}", $"{Ref.Name}\nSon etkinlik: {LastActivity}\n{Ref.Subject}") + (Status.Length > 0 ? Loc.T("\nStatus: ", "\nDurum: ") + Status : "");

    [RelayCommand] private Task Switch() => Owner.CheckoutAsync(Ref);
    [RelayCommand] private Task Merge() => Owner.IntegrateAsync(Ref, IntegrationKind.Merge);
    [RelayCommand] private Task Rebase() => Owner.IntegrateAsync(Ref, IntegrationKind.Rebase);
    [RelayCommand] private Task Rename() => Owner.RenameBranchAsync(Ref);
    [RelayCommand] private Task Delete() => IsTag ? Owner.DeleteTagAsync(Ref) : Owner.DeleteBranchAsync(Ref);
    [RelayCommand] private Task CreateBranchFrom() => Owner.CreateBranch(Ref.TargetSha);
    [RelayCommand] private Task ForcePush() => Owner.ForcePushAsync(Ref);
    [RelayCommand] private Task CopyName() => Owner.Shell.CopyToClipboardAsync(Ref.Name);

    [RelayCommand]
    private void Reveal() => Owner.Workspace.RevealCommit(Ref.TargetSha);
}

public sealed partial class StashItemViewModel(RepositoryViewModel owner, StashEntry stash) : ObservableObject
{
    public StashEntry Stash { get; } = stash;
    public string Message => Stash.Message;
    public string Date => Format.Relative(Stash.Date);

    [RelayCommand] private Task Apply() => owner.ApplyStashAsync(Stash, pop: false);
    [RelayCommand] private Task Pop() => owner.ApplyStashAsync(Stash, pop: true);
    [RelayCommand] private Task Drop() => owner.DropStashAsync(Stash);
}

/// <summary>Ana çalışma ekranı: branch'ler, commit grafiği, çalışma alanı özeti ve detay paneli.</summary>
public sealed partial class WorkspaceViewModel : ViewModelBase
{
    private const int PageSize = 600;
    private readonly RepositoryViewModel _owner;
    private CommitGraphLayout _layout = new();
    private int _loadedCount;
    private CommitRowViewModel? _workingTreeRow;
    private IReadOnlyList<RefInfo> _refs = [];
    private HeadInfo _head = new(null, null);

    public WorkspaceViewModel(RepositoryViewModel owner)
    {
        _owner = owner;
        Changes = new ChangesViewModel(owner);
        GuidedActions = new GuidedActionsViewModel(owner);
    }

    public ObservableCollection<BranchItemViewModel> LocalBranches { get; } = [];
    public ObservableCollection<BranchItemViewModel> RemoteBranches { get; } = [];
    public ObservableCollection<BranchItemViewModel> Tags { get; } = [];
    public ObservableCollection<StashItemViewModel> Stashes { get; } = [];
    public ObservableCollection<CommitRowViewModel> Rows { get; } = [];
    public ChangesViewModel Changes { get; }
    public GuidedActionsViewModel GuidedActions { get; }

    [ObservableProperty]
    public partial CommitRowViewModel? SelectedRow { get; set; }

    [ObservableProperty]
    public partial ViewModelBase? Details { get; set; }

    [ObservableProperty]
    public partial double GraphWidth { get; set; } = 40;

    [ObservableProperty]
    public partial bool HasMore { get; set; }

    [ObservableProperty]
    public partial bool ShowRemotes { get; set; }

    [ObservableProperty]
    public partial bool ShowTags { get; set; }

    [ObservableProperty]
    public partial bool ShowStashes { get; set; } = true;

    [ObservableProperty]
    public partial string BranchFilter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int ModifiedCount { get; set; }

    [ObservableProperty]
    public partial int UntrackedCount { get; set; }

    [ObservableProperty]
    public partial int StagedCount { get; set; }

    [ObservableProperty]
    public partial int ConflictedCount { get; set; }

    public bool IsClean => ModifiedCount + UntrackedCount + StagedCount + ConflictedCount == 0;
    public bool HasConflicted => ConflictedCount > 0;
    public string WorkingTreeTitle => _owner.IsPro ? Loc.T("WORKING TREE", "ÇALIŞMA ALANI") : Loc.T("YOUR CHANGES", "DEĞİŞİKLİKLERİNİZ");
    public string RemoteHeader => Loc.T($"REMOTE BRANCHES ({RemoteBranches.Count})", $"UZAK BRANCH'LER ({RemoteBranches.Count})");
    public string TagHeader => Loc.T($"TAGS ({Tags.Count})", $"TAG'LER ({Tags.Count})");
    public string StashHeader => _owner.IsPro ? Loc.T($"STASHES ({Stashes.Count})", $"STASH'LER ({Stashes.Count})") : Loc.T($"PUT ASIDE ({Stashes.Count})", $"KENARA KONANLAR ({Stashes.Count})");

    public void OnModeChanged()
    {
        OnPropertyChanged(nameof(WorkingTreeTitle));
        OnPropertyChanged(nameof(StashHeader));
        BuildBranchLists();
        Changes.OnModeChanged();
    }

    public async Task UpdateAsync(RepositoryStatus status, IReadOnlyList<RefInfo> refs, IReadOnlyList<StashEntry> stashes, HeadInfo head, bool reloadHistory)
    {
        _refs = refs;
        _head = head;
        ModifiedCount = status.ModifiedCount;
        UntrackedCount = status.UntrackedCount;
        StagedCount = status.StagedCount;
        ConflictedCount = status.ConflictedCount;
        OnPropertyChanged(nameof(IsClean));
        OnPropertyChanged(nameof(HasConflicted));

        BuildBranchLists();
        Stashes.Clear();
        foreach (var stash in stashes) Stashes.Add(new StashItemViewModel(_owner, stash));
        OnPropertyChanged(nameof(StashHeader));

        Changes.Update(status);

        if (reloadHistory) await LoadHistoryAsync(reset: true);
        else UpdateWorkingTreeRow(status);

        if (Details is CommitDetailsViewModel && SelectedRow is null) Details = null;
        if (ConflictedCount > 0 && Details is not ConflictResolverViewModel && _owner.IsOperationInProgress) { }
    }

    partial void OnBranchFilterChanged(string value) => BuildBranchLists();

    private void BuildBranchLists()
    {
        var filter = BranchFilter.Trim();
        bool Matches(RefInfo r) => filter.Length == 0 || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var guided = !_owner.IsPro;

        LocalBranches.Clear();
        foreach (var r in _refs.Where(r => r.Kind == RefKind.LocalBranch && Matches(r)).OrderByDescending(r => r.IsHead).ThenBy(r => r.Name))
            LocalBranches.Add(new BranchItemViewModel(_owner, r, guided));
        RemoteBranches.Clear();
        foreach (var r in _refs.Where(r => r.Kind == RefKind.RemoteBranch && Matches(r)).OrderBy(r => r.Name))
            RemoteBranches.Add(new BranchItemViewModel(_owner, r, guided));
        Tags.Clear();
        foreach (var r in _refs.Where(r => r.Kind == RefKind.Tag && Matches(r)).OrderByDescending(r => r.LastCommitDate))
            Tags.Add(new BranchItemViewModel(_owner, r, guided));
        OnPropertyChanged(nameof(RemoteHeader));
        OnPropertyChanged(nameof(TagHeader));
    }

    private async Task LoadHistoryAsync(bool reset)
    {
        var selectedSha = SelectedRow?.Commit?.Sha;
        var wasWorkingTree = SelectedRow?.IsWorkingTree == true;
        var git = _owner.Session.Git;
        var skip = reset ? 0 : _loadedCount;

        IReadOnlyList<CommitInfo> commits;
        try
        {
            commits = await Task.Run(() => git.History.GetCommitsAsync(new LogQuery { MaxCount = PageSize, Skip = skip }));
        }
        catch (GitException ex)
        {
            _owner.Shell.ShowToast(new ToastViewModel(Loc.T("Could not load history.", "Geçmiş yüklenemedi."), ToastKind.Warning, ex.Error.Title));
            return;
        }

        if (reset)
        {
            _layout = new CommitGraphLayout();
            _loadedCount = 0;
            Rows.Clear();
            _workingTreeRow = null;
        }

        var graph = _layout.Append(commits);
        var badges = BuildBadgeMap();
        var rows = new List<CommitRowViewModel>(commits.Count);
        for (var i = 0; i < commits.Count; i++)
        {
            var commit = commits[i];
            rows.Add(new CommitRowViewModel(_owner, commit, graph[i], badges.GetValueOrDefault(commit.Sha, []), commit.Sha == _head.Sha));
        }

        _loadedCount += commits.Count;
        HasMore = commits.Count == PageSize;
        var maxLanes = Math.Max(rows.Count == 0 ? 1 : rows.Max(r => r.Graph.LaneCount), Rows.Count == 0 ? 1 : Rows.Where(r => r.IsCommit).Select(r => r.Graph.LaneCount).DefaultIfEmpty(1).Max());
        GraphWidth = CommitGraphCell.LeftPadding * 2 + Math.Min(maxLanes, 14) * CommitGraphCell.LaneWidth;

        foreach (var row in rows) Rows.Add(row);
        UpdateWorkingTreeRow(_owner.Status);

        if (reset)
        {
            var reselect = wasWorkingTree ? _workingTreeRow : Rows.FirstOrDefault(r => r.Commit?.Sha == selectedSha);
            if (reselect is not null) SelectedRow = reselect;
            else if (selectedSha is not null || wasWorkingTree) SelectInitialRow();
        }
    }

    private Dictionary<string, List<RefBadge>> BuildBadgeMap()
    {
        var map = new Dictionary<string, List<RefBadge>>(StringComparer.Ordinal);
        foreach (var r in _refs.OrderBy(r => r.Kind))
        {
            if (!map.TryGetValue(r.TargetSha, out var list)) map[r.TargetSha] = list = [];
            list.Add(new RefBadge(r.Name, r.Kind, r.IsHead));
        }
        return map;
    }

    private void UpdateWorkingTreeRow(RepositoryStatus? status)
    {
        var dirty = status is { IsClean: false };
        if (!dirty)
        {
            if (_workingTreeRow is not null)
            {
                if (SelectedRow == _workingTreeRow) SelectedRow = Rows.FirstOrDefault(r => r.IsHead);
                Rows.Remove(_workingTreeRow);
                _workingTreeRow = null;
            }
            return;
        }

        var headRow = Rows.FirstOrDefault(r => r.IsHead);
        var lane = headRow?.Graph.NodeLane ?? 0;
        var summary = DescribeWorkingTree(status!);
        if (_workingTreeRow is null)
        {
            _workingTreeRow = new CommitRowViewModel(_owner, null, new GraphRow(lane, 0, [], lane + 1, false), [], false) { WorkingTreeSummary = summary };
            Rows.Insert(0, _workingTreeRow);
        }
        else
        {
            _workingTreeRow.WorkingTreeSummary = summary;
            _workingTreeRow.NotifySubject();
        }
    }

    private string DescribeWorkingTree(RepositoryStatus status)
    {
        var parts = new List<string>();
        if (status.ConflictedCount > 0) parts.Add(Loc.T($"{status.ConflictedCount} conflicted", $"{status.ConflictedCount} çakışmalı"));
        if (status.StagedCount > 0) parts.Add(Loc.T($"{status.StagedCount} staged", $"{status.StagedCount} stage edildi"));
        if (status.ModifiedCount > 0) parts.Add(Loc.T($"{status.ModifiedCount} modified", $"{status.ModifiedCount} değişti"));
        if (status.UntrackedCount > 0) parts.Add(Loc.T($"{status.UntrackedCount} new", $"{status.UntrackedCount} yeni"));
        var title = _owner.IsPro ? Loc.T("Uncommitted changes", "Commit edilmemiş değişiklikler") : Loc.T("Your unsaved work", "Kaydedilmemiş çalışmanız");
        return $"{title} · {string.Join(", ", parts)}";
    }

    public void SelectInitialRow() => SelectedRow = _workingTreeRow ?? Rows.FirstOrDefault(r => r.IsHead) ?? Rows.FirstOrDefault();

    partial void OnSelectedRowChanged(CommitRowViewModel? value)
    {
        if (value is null)
        {
            Details = _owner.IsPro ? null : GuidedActions;
            return;
        }
        if (value.IsWorkingTree)
        {
            Details = _owner.IsOperationInProgress && ConflictedCount > 0 ? new ConflictResolverViewModel(_owner) : Changes;
            return;
        }
        var details = new CommitDetailsViewModel(_owner, value.Commit!);
        Details = details;
        _ = details.LoadAsync();
    }

    [RelayCommand]
    private void SelectWorkingTree()
    {
        if (_workingTreeRow is not null) SelectedRow = _workingTreeRow;
        else
        {
            SelectedRow = null;
            Details = Changes;
        }
    }

    public void ShowConflicts()
    {
        if (_workingTreeRow is not null && SelectedRow != _workingTreeRow) SelectedRow = _workingTreeRow;
        Details = new ConflictResolverViewModel(_owner);
    }

    public void RevealCommit(string sha)
    {
        var row = Rows.FirstOrDefault(r => r.Commit?.Sha == sha);
        if (row is not null) SelectedRow = row;
        else _owner.Shell.ShowToast(new ToastViewModel(Loc.T("That commit is not in the loaded history.", "Bu commit yüklenen geçmişte yok."), ToastKind.Info));
    }

    [RelayCommand]
    private Task LoadMore() => LoadHistoryAsync(reset: false);

    [RelayCommand]
    private void ToggleRemotes() => ShowRemotes = !ShowRemotes;

    [RelayCommand]
    private void ToggleTags() => ShowTags = !ShowTags;

    [RelayCommand]
    private void ToggleStashes() => ShowStashes = !ShowStashes;
}

public sealed record GuidedAction(string Title, string Description, string IconKey, IRelayCommand Command);

/// <summary>Guided modda hiçbir şey seçili değilken: "What do you want to do?"</summary>
public sealed partial class GuidedActionsViewModel(RepositoryViewModel owner) : ViewModelBase
{
    public IReadOnlyList<GuidedAction> Actions { get; } =
    [
        new(Loc.T("Save my current work", "Mevcut çalışmamı kaydet"), Loc.T("Choose files and describe what you did (commit).", "Dosyaları seçin ve ne yaptığınızı anlatın (commit)."), "Icon.Save", new RelayCommand(() => owner.Workspace.SelectWorkingTreeCommand.Execute(null))),
        new(Loc.T("Create a branch", "Branch oluştur"), Loc.T("Start a separate line of work.", "Ayrı bir çalışma hattı başlatın."), "Icon.Branch", new AsyncRelayCommand(() => owner.CreateBranch(null))),
        new(Loc.T("Combine these changes", "Bu değişiklikleri birleştir"), Loc.T("Bring another branch's work into yours (merge).", "Başka bir branch'in çalışmasını sizinkine getirin (merge)."), "Icon.Merge", new RelayCommand(() => owner.Shell.ShowOverlay(CommandPaletteViewModel.PickBranch(owner.Shell, Loc.T("WHICH BRANCH DO YOU WANT TO COMBINE INTO YOURS?", "HANGİ BRANCH'İ SİZİNKİYLE BİRLEŞTİRMEK İSTİYORSUNUZ?"), r => owner.IntegrateAsync(r, Dialogs.IntegrationKind.Merge))))),
        new(Loc.T("Undo last operation", "Son işlemi geri al"), Loc.T("Go back to how things were before.", "Her şeyi önceki hâline döndürün."), "Icon.Undo", new AsyncRelayCommand(() => owner.UndoCommand.ExecuteAsync(null))),
        new(Loc.T("See what changed", "Neler değişti, görün"), Loc.T("Browse commits and their differences.", "Commit'leri ve farklarını inceleyin."), "Icon.Graph", new RelayCommand(() => owner.Workspace.SelectedRow = owner.Workspace.Rows.FirstOrDefault(r => r.IsHead))),
        new(Loc.T("Restore an old version", "Eski bir sürümü geri getir"), Loc.T("Pick a moment from the Time Machine.", "Zaman Makinesi'nden bir an seçin."), "Icon.TimeMachine", new RelayCommand(() => owner.Section = RepositorySection.TimeMachine)),
        new(Loc.T("Recover something I lost", "Kaybettiğim bir şeyi kurtar"), Loc.T("Deleted branch, reset, dropped stash…", "Silinen branch, reset, düşürülen stash…"), "Icon.Recovery", new RelayCommand(() => owner.Section = RepositorySection.Recovery)),
        new(Loc.T("Find when this bug was introduced", "Bu hatanın ne zaman girdiğini bul"), Loc.T("Visual bisect — coming in the next release.", "Görsel bisect — sonraki sürümde geliyor."), "Icon.Search", new RelayCommand(() =>
            owner.Shell.ShowToast(new ToastViewModel(Loc.T("Visual bisect is planned for Phase 2.", "Görsel bisect 2. aşama için planlanıyor."), ToastKind.Info, Loc.T("Meanwhile, the Time Machine and file history help narrow it down.", "Bu arada Zaman Makinesi ve dosya geçmişi aralığı daraltmaya yardımcı olur."))))),
    ];
}
