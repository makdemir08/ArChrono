using System.Collections.ObjectModel;
using System.Text;
using ArChrono.AI.Context;
using ArChrono.AI.Privacy;
using ArChrono.AI.Providers;
using ArChrono.App.Infrastructure;
using ArChrono.Application.Search;
using ArChrono.Application.Settings;
using ArChrono.Git.Errors;
using ArChrono.Git.Models;
using ArChrono.Git.Process;
using ArChrono.Git.Services;
using ArChrono.Localization;
using ArChrono.Platform.Credentials;
using ArChrono.Platform.Shell;
using ArChrono.Recovery.Retention;
using ArChrono.Storage.Records;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArChrono.App.ViewModels;

public sealed record PaletteItem(string Title, string? Subtitle, string IconKey, string Category, string? Shortcut, Func<Task> Execute, double Score = 0)
{
    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);
    public bool HasShortcut => !string.IsNullOrEmpty(Shortcut);
}

/// <summary>
/// VS Code benzeri komut paleti. Önekler: ">" komut, "@" branch, "#" commit, "/" dosya, "~" recovery/snapshot.
/// Guided modda boş sorgu "What do you want to do?" eylemlerini gösterir.
/// </summary>
public sealed partial class CommandPaletteViewModel : ViewModelBase, IOverlay
{
    private readonly MainWindowViewModel _shell;
    private readonly Func<RefInfo, Task>? _branchAction;
    private readonly string? _branchTitle;
    private CancellationTokenSource? _search;

    public CommandPaletteViewModel(MainWindowViewModel shell, string initialQuery, Func<RefInfo, Task>? branchAction = null, string? branchTitle = null)
    {
        _shell = shell;
        _branchAction = branchAction;
        _branchTitle = branchTitle;
        Query = initialQuery;
        Update();
    }

    /// <summary>"Hangi branch'i birleştireyim?" gibi branch seçme modunda palet.</summary>
    public static CommandPaletteViewModel PickBranch(MainWindowViewModel shell, string title, Func<RefInfo, Task> action) => new(shell, "@", action, title);

    public ObservableCollection<PaletteItem> Items { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; }

    [ObservableProperty]
    public partial PaletteItem? Selected { get; set; }

    [ObservableProperty]
    public partial string SectionTitle { get; set; } = string.Empty;

    public string Watermark => _shell.Repository is null ? Loc.T("Type a command…", "Bir komut yazın…") : Loc.T("Search commands, branches, commits, files, recovery points…   ( > @ # / ~ )", "Komut, branch, commit, dosya veya kurtarma noktası arayın…   ( > @ # / ~ )");

    public void Dismiss() => _shell.Overlay = null;

    partial void OnQueryChanged(string value) => Update();

    private void Update()
    {
        _search?.Cancel();
        var query = Query.Trim();
        var prefix = query.Length > 0 && query[0] is '>' or '@' or '#' or '/' or '~' ? query[0] : '\0';
        var text = prefix == '\0' ? query : query[1..].Trim();
        var repo = _shell.Repository;

        var results = new List<PaletteItem>();
        if (prefix is '\0' or '>')
        {
            var commands = BuildCommands();
            if (text.Length == 0 && prefix == '\0' && !_shell.IsPro && repo is not null)
            {
                SectionTitle = Loc.T("WHAT DO YOU WANT TO DO?", "NE YAPMAK İSTİYORSUNUZ?");
                results.AddRange(commands.Where(c => c.Category == "Guided"));
            }
            else
            {
                SectionTitle = text.Length == 0 ? Loc.T("COMMANDS", "KOMUTLAR") : Loc.T("RESULTS", "SONUÇLAR");
                results.AddRange(commands
                    .Where(c => c.Category != "Guided" || text.Length > 0)
                    .Select(c => c with { Score = text.Length == 0 ? 1 : Math.Max(FuzzyMatcher.Score(text, c.Title), FuzzyMatcher.Score(text, c.Subtitle ?? "") * 0.5) })
                    .Where(c => c.Score > 0)
                    .OrderByDescending(c => c.Score)
                    .DistinctBy(c => c.Title));
            }
        }
        SetItems(results);

        if (repo is null || prefix == '>' || text.Length < (prefix == '\0' ? 2 : 0)) return;
        var kinds = prefix switch
        {
            '@' => new HashSet<SearchResultKind> { SearchResultKind.Branch, SearchResultKind.Tag },
            '#' => new HashSet<SearchResultKind> { SearchResultKind.Commit },
            '/' => new HashSet<SearchResultKind> { SearchResultKind.File },
            '~' => new HashSet<SearchResultKind> { SearchResultKind.RecoveryPoint },
            _ => null,
        };
        if (prefix == '@' && text.Length == 0)
        {
            SectionTitle = _branchTitle ?? Loc.T("BRANCHES", "BRANCH'LER");
            SetItems(repo.Refs.Where(r => r.Kind is RefKind.LocalBranch or RefKind.RemoteBranch && (_branchAction is null || !r.IsHead)).Take(60).Select(r => BranchItem(repo, r)).ToList());
            return;
        }

        _search = new CancellationTokenSource();
        var token = _search.Token;
        _ = Task.Run(async () =>
        {
            await Task.Delay(120, token);
            var found = await repo.Session.Search.SearchAsync(text, kinds, 8, token);
            OnUi(() =>
            {
                if (token.IsCancellationRequested) return;
                var combined = results.Concat(found.Select(f => ToItem(repo, f))).OrderByDescending(i => i.Score).Take(60).ToList();
                SectionTitle = Loc.T("RESULTS", "SONUÇLAR");
                SetItems(combined);
            });
        }, token);
    }

    private void SetItems(IReadOnlyList<PaletteItem> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        Selected = Items.FirstOrDefault();
    }

    public void MoveSelection(int delta)
    {
        if (Items.Count == 0) return;
        var index = Selected is null ? 0 : Items.IndexOf(Selected);
        Selected = Items[Math.Clamp(index + delta, 0, Items.Count - 1)];
    }

    [RelayCommand]
    public async Task Execute(PaletteItem? item)
    {
        item ??= Selected;
        if (item is null) return;
        _shell.Overlay = null;
        await item.Execute();
    }

    private PaletteItem ToItem(RepositoryViewModel repo, SearchResult result) => result.Kind switch
    {
        SearchResultKind.Branch or SearchResultKind.Tag => BranchItem(repo, (RefInfo)result.Payload) with { Score = result.Score },
        SearchResultKind.Commit or SearchResultKind.ChangedCode => new PaletteItem(result.Title, result.Subtitle, "Icon.Commit", "Commit", null, () =>
        {
            repo.Section = RepositorySection.Workspace;
            repo.Workspace.RevealCommit(((CommitInfo)result.Payload).Sha);
            return Task.CompletedTask;
        }, result.Score),
        SearchResultKind.File => new PaletteItem(result.Title, result.Subtitle, "Icon.File", "File", null, () =>
        {
            repo.Section = RepositorySection.TimeMachine;
            repo.TimeMachine.FileQuery = (string)result.Payload;
            return repo.TimeMachine.LookupFileCommand.ExecuteAsync(null);
        }, result.Score),
        _ => new PaletteItem(result.Title, result.Subtitle, "Icon.Recovery", "Recovery", null,
            () => repo.ShowRecoveryPointAsync(((RecoveryPointRecord)result.Payload).Id), result.Score),
    };

    private PaletteItem BranchItem(RepositoryViewModel repo, RefInfo reference) =>
        new(reference.Name, reference.Kind == RefKind.Tag ? "Tag · " + reference.Subject : (reference.IsHead ? Loc.T("Current branch · ", "Mevcut branch · ") : Loc.T("Switch · ", "Geç · ")) + reference.Subject,
            reference.Kind == RefKind.Tag ? "Icon.Tag" : "Icon.Branch", "Branch", null,
            () => _branchAction is not null ? _branchAction(reference)
                : reference.IsHead || reference.Kind == RefKind.Tag ? Task.CompletedTask : repo.CheckoutAsync(reference), 10);

    private List<PaletteItem> BuildCommands()
    {
        var pro = _shell.IsPro;
        var repo = _shell.Repository;
        var list = new List<PaletteItem>();
        void Add(string proTitle, string guidedTitle, string icon, string? shortcut, Func<Task> run, string? subtitle = null) =>
            list.Add(new PaletteItem(pro ? proTitle : guidedTitle, pro ? subtitle : proTitle == guidedTitle ? subtitle : proTitle, icon, "Command", shortcut, run));

        if (repo is not null)
        {
            var guided = repo.Workspace.GuidedActions.Actions;
            list.AddRange(guided.Select(a => new PaletteItem(a.Title, a.Description, a.IconKey, "Guided", null, () =>
            {
                repo.Section = RepositorySection.Workspace;
                if (a.Command is IAsyncRelayCommand asyncCommand) return asyncCommand.ExecuteAsync(null);
                a.Command.Execute(null);
                return Task.CompletedTask;
            })));

            Add(Loc.T("Commit", "Commit"), Loc.T("Save my current work", "Mevcut çalışmamı kaydet"), "Icon.Save", "⌘↵", () => { repo.Workspace.SelectWorkingTreeCommand.Execute(null); return Task.CompletedTask; });
            Add(Loc.T("Create Branch", "Branch oluştur"), Loc.T("Create a branch", "Branch oluştur"), "Icon.Branch", "⌘B", () => repo.CreateBranch(null));
            Add(Loc.T("Switch Branch", "Branch değiştir"), Loc.T("Switch to another branch", "Başka bir branch'e geç"), "Icon.Branch", null, () => { Query = "@"; _shell.ShowOverlay(this); return Task.CompletedTask; });
            Add(Loc.T("Merge Branch", "Branch merge et"), Loc.T("Combine these changes", "Bu değişiklikleri birleştir"), "Icon.Merge", null, () =>
            {
                _shell.ShowOverlay(PickBranch(_shell, Loc.T($"MERGE WHICH BRANCH INTO {repo.BranchDisplay.ToUpperInvariant()}?", $"{repo.BranchDisplay.ToUpperInvariant()} İÇİNE HANGİ BRANCH MERGE EDİLSİN?"), r => repo.IntegrateAsync(r, Dialogs.IntegrationKind.Merge)));
                return Task.CompletedTask;
            });
            Add(Loc.T("Rebase", "Rebase"), Loc.T("Move my commits on top of another branch", "Commit'lerimi başka bir branch'in üzerine taşı"), "Icon.Rebase", null, () =>
            {
                _shell.ShowOverlay(PickBranch(_shell, Loc.T($"REBASE {repo.BranchDisplay.ToUpperInvariant()} ONTO…", $"{repo.BranchDisplay.ToUpperInvariant()} HANGİ BRANCH'İN ÜZERİNE REBASE EDİLSİN?"), r => repo.IntegrateAsync(r, Dialogs.IntegrationKind.Rebase)));
                return Task.CompletedTask;
            });
            Add(Loc.T("Stash Changes", "Değişiklikleri stash'le"), Loc.T("Put my changes aside", "Değişikliklerimi kenara koy"), "Icon.Stash", "⌘⇧S", () => repo.StashCommand.ExecuteAsync(null));
            Add(Loc.T("Undo", "Geri al"), Loc.T("Undo last operation", "Son işlemi geri al"), "Icon.Undo", "⌘Z", () => repo.UndoCommand.ExecuteAsync(null), repo.UndoTitle);
            Add(Loc.T("Redo", "Yinele"), Loc.T("Redo", "Yinele"), "Icon.Redo", "⌘⇧Z", () => repo.RedoCommand.ExecuteAsync(null), repo.RedoTitle);
            Add(Loc.T("Fetch", "Fetch"), Loc.T("Check the server for new changes", "Sunucuda yeni değişiklikleri kontrol et"), "Icon.Fetch", "⌥⌘F", () => repo.FetchCommand.ExecuteAsync(null));
            Add(Loc.T("Pull", "Pull"), Loc.T("Get the latest changes", "Son değişiklikleri al"), "Icon.Pull", "⌥⌘L", () => repo.PullCommand.ExecuteAsync(null));
            Add(Loc.T("Push", "Push"), Loc.T("Send my commits to the server", "Commit'lerimi sunucuya gönder"), "Icon.Push", "⌥⌘P", () => repo.PushCommand.ExecuteAsync(null));
            Add(Loc.T("Recover Lost Commit", "Kayıp commit'i kurtar"), Loc.T("Recover something I lost", "Kaybettiğim bir şeyi kurtar"), "Icon.Lost", null, async () =>
            {
                repo.Section = RepositorySection.Recovery;
                await repo.Recovery.DeepScanCommand.ExecuteAsync(null);
            });
            Add(Loc.T("Open Reflog", "Reflog'u aç"), Loc.T("Everything HEAD has pointed to", "HEAD'in gösterdiği her şey"), "Icon.Reflog", "⌘2", () =>
            {
                repo.Section = RepositorySection.Recovery;
                repo.Recovery.ShowReflog = true;
                return Task.CompletedTask;
            });
            Add(Loc.T("Open Recovery Center", "Kurtarma Merkezi'ni aç"), Loc.T("Open Recovery Center", "Kurtarma Merkezi'ni aç"), "Icon.Recovery", "⌘2", () => { repo.Section = RepositorySection.Recovery; return Task.CompletedTask; });
            Add(Loc.T("Restore Snapshot", "Anlık görüntüyü geri yükle"), Loc.T("Restore an old version", "Eski bir sürümü geri getir"), "Icon.TimeMachine", "⌘3", () => { repo.Section = RepositorySection.TimeMachine; return Task.CompletedTask; });
            Add(Loc.T("Compare Snapshots", "Anlık görüntüleri karşılaştır"), Loc.T("Compare two moments", "İki anı karşılaştır"), "Icon.TimeMachine", null, () =>
            {
                repo.Section = RepositorySection.TimeMachine;
                repo.TimeMachine.CompareWithPrevious = true;
                return Task.CompletedTask;
            });
            Add(Loc.T("Save Snapshot Now", "Şimdi anlık görüntü kaydet"), Loc.T("Save a snapshot now", "Şimdi anlık görüntü kaydet"), "Icon.Snapshot", null, () => repo.SaveSnapshotCommand.ExecuteAsync(null));
            Add(Loc.T("Save Recovery Point", "Kurtarma noktası kaydet"), Loc.T("Save a recovery point", "Kurtarma noktası kaydet"), "Icon.Pin", null, () => repo.Recovery.SaveRecoveryPointCommand.ExecuteAsync(null));
            Add(Loc.T("Explain Commit", "Commit'i açıkla"), Loc.T("Explain this commit", "Bu commit'i açıkla"), "Icon.Sparkles", "⌘E", () =>
            {
                if (repo.Workspace.Details is CommitDetailsViewModel details) details.ShowExplanation();
                else _shell.ShowToast(new ToastViewModel(Loc.T("Select a commit first.", "Önce bir commit seçin."), ToastKind.Info));
                return Task.CompletedTask;
            });
            Add(Loc.T("Explain Branch Changes", "Branch değişikliklerini açıkla"), Loc.T("Explain what this branch changes", "Bu branch'in neyi değiştirdiğini açıkla"), "Icon.Sparkles", null, () => ExplainBranchAsync(repo));
            Add(Loc.T("Go to Workspace", "Çalışma alanına git"), Loc.T("Go to workspace", "Çalışma alanına git"), "Icon.Graph", "⌘1", () => { repo.Section = RepositorySection.Workspace; return Task.CompletedTask; });
            Add(Loc.T("Open Terminal Here", "Burada terminal aç"), Loc.T("Open a terminal in this folder", "Bu klasörde terminal aç"), "Icon.Terminal", "⌘J", () => { repo.OpenTerminalCommand.Execute(null); return Task.CompletedTask; });
            Add(Loc.T("Toggle Git Console", "Git Konsolunu aç/kapat"), Loc.T("Show Git commands", "Git komutlarını göster"), "Icon.Terminal", "⌘`", () => { repo.ToggleConsoleCommand.Execute(null); return Task.CompletedTask; });
            Add(Loc.T("Reveal in File Manager", "Dosya yöneticisinde göster"), Loc.T("Show folder", "Klasörü göster"), "Icon.Folder", null, () => { repo.RevealInFileManagerCommand.Execute(null); return Task.CompletedTask; });
            if (repo.IsOperationInProgress)
            {
                Add(Loc.T("Continue Operation", "İşleme devam et"), Loc.T("Continue", "Devam et"), "Icon.Check", null, () => repo.ContinueCommand.ExecuteAsync(null), repo.StateTitle);
                Add(Loc.T("Abort Operation", "İşlemi iptal et"), Loc.T("Stop and go back", "Durdur ve geri dön"), "Icon.Close", null, () => repo.AbortCommand.ExecuteAsync(null), repo.StateTitle);
            }
            Add(Loc.T("Find Bug with Bisect", "Bisect ile hata bul"), Loc.T("Find when this bug was introduced", "Bu hatanın ne zaman girdiğini bul"), "Icon.Search", null, () =>
            {
                _shell.ShowToast(new ToastViewModel(Loc.T("Visual bisect is planned for Phase 2.", "Görsel bisect 2. aşama için planlanıyor."), ToastKind.Info));
                return Task.CompletedTask;
            }, Loc.T("Coming in Phase 2", "2. aşamada geliyor"));
            Add(Loc.T("Create Worktree", "Worktree oluştur"), Loc.T("Work on two branches at once", "Aynı anda iki branch üzerinde çalış"), "Icon.Branch", null, () =>
            {
                _shell.ShowToast(new ToastViewModel(Loc.T("Worktrees and agent workspaces are planned for Phase 2.", "Worktree'ler ve agent çalışma alanları 2. aşama için planlanıyor."), ToastKind.Info));
                return Task.CompletedTask;
            }, Loc.T("Coming in Phase 2", "2. aşamada geliyor"));
            Add(Loc.T("Remove ArChrono Data from Repository", "ArChrono verilerini depodan kaldır"), Loc.T("Remove ArChrono data from this repository", "ArChrono verilerini bu depodan kaldır"), "Icon.Discard", null, () => RemoveDataAsync(repo),
                Loc.T("Deletes refs/archrono/* pins (recovery points may become incomplete)", "refs/archrono/* pin'lerini siler (kurtarma noktaları eksik kalabilir)"));
            Add(Loc.T("Close Repository", "Depoyu kapat"), Loc.T("Close repository", "Depoyu kapat"), "Icon.Home", null, () => _shell.CloseRepository());
        }

        Add(Loc.T("Open Repository…", "Depo aç…"), Loc.T("Open a repository", "Bir depo aç"), "Icon.Folder", "⌘O", async () =>
        {
            if (await _shell.PickFolderAsync(Loc.T("Open a Git repository", "Bir Git deposu aç")) is { } path) await _shell.OpenRepositoryAsync(path);
        });
        Add(Loc.T("Clone Repository…", "Depo klonla…"), Loc.T("Clone a repository", "Bir depoyu klonla"), "Icon.Download", null, () => _shell.Home.CloneCommand.ExecuteAsync(null));
        Add(Loc.T("Settings", "Ayarlar"), Loc.T("Settings", "Ayarlar"), "Icon.Settings", "⌘,", () => _shell.OpenSettings());
        Add(Loc.T("Switch to Pro Mode", "Pro moda geç"), Loc.T("Switch to Pro mode (Git terminology)", "Pro moda geç (Git terimleri)"), "Icon.Zap", null, () => { _shell.IsPro = true; return Task.CompletedTask; });
        Add(Loc.T("Switch to Guided Mode", "Rehberli moda geç"), Loc.T("Switch to Guided mode", "Rehberli moda geç"), "Icon.Compass", null, () => { _shell.IsPro = false; return Task.CompletedTask; });
        Add(Loc.T("Toggle Day/Night Mode", "Gece/Gündüz Modunu Değiştir"), Loc.T("Switch between day and night mode", "Gece ve gündüz modu arasında geç"), "Icon.Moon", null,
            () => { _shell.ToggleDayNight(); return Task.CompletedTask; });
        Add(Loc.T("Switch Language to Turkish (Türkçe)", "Dili İngilizce Yap (English)"), Loc.T("Show the interface in Turkish (Türkçe)", "Arayüzü İngilizce göster (English)"), "Icon.Globe", null,
            () => { _shell.SetLanguage(Loc.IsTurkish ? LanguagePreference.English : LanguagePreference.Turkish); return Task.CompletedTask; });
        Add(Loc.T("About ArChrono", "ArChrono Hakkında"), Loc.T("About ArChrono", "ArChrono Hakkında"), "Icon.Info", null, () => { _shell.ShowAboutCommand.Execute(null); return Task.CompletedTask; });
        return list;
    }

    private async Task ExplainBranchAsync(RepositoryViewModel repo)
    {
        if (repo.Head.BranchName is not { } branch)
        {
            _shell.ShowToast(new ToastViewModel(Loc.T("Switch to a branch first.", "Önce bir branch'e geçin."), ToastKind.Info));
            return;
        }
        var baseBranch = repo.Refs.FirstOrDefault(r => r.Kind == RefKind.LocalBranch && r.Name is "main" or "master" or "develop" && r.Name != branch)?.Name;
        if (baseBranch is null)
        {
            _shell.ShowToast(new ToastViewModel(Loc.T("No main/master/develop branch to compare with.", "Karşılaştırılacak main/master/develop branch'i yok."), ToastKind.Info));
            return;
        }
        var explanation = new AiExplanationViewModel(repo, Loc.T($"Source: commits on {branch} that are not on {baseBranch}", $"Kaynak: {branch} üzerinde olup {baseBranch} üzerinde olmayan commit'ler"),
            token => _shell.Services.Ai.PrepareExplainBranchAsync(repo.Session, branch, baseBranch, token));
        await _shell.ShowDialogAsync(new Dialogs.AiExplanationDialogViewModel(Loc.T($"What does {branch} change?", $"{branch} neyi değiştiriyor?"), explanation));
    }

    private async Task RemoveDataAsync(RepositoryViewModel repo)
    {
        var confirm = new Dialogs.ConfirmOperationDialogViewModel(Loc.T("REMOVE ARCHRONO DATA", "ARCHRONO VERİLERİNİ KALDIR"), Loc.T("Remove ArChrono data from this repository", "ArChrono verilerini bu depodan kaldır"),
            [Loc.T("All refs/archrono/* pin refs will be deleted from this repository.", "Bu depodaki tüm refs/archrono/* pin ref'leri silinecek."), Loc.T("Recovery points stay listed, but commits they point to may be cleaned up by git gc later.", "Kurtarma noktaları listede kalır, ancak gösterdikleri commit'ler daha sonra git gc tarafından temizlenebilir.")],
            "git update-ref -d refs/archrono/…", _shell.IsPro, createsRecoveryPoint: false, confirmText: Loc.T("Remove", "Kaldır"));
        if (await _shell.ShowDialogAsync(confirm) is not { Accepted: true }) return;
        var removed = await Task.Run(() => _shell.Services.Recovery.Pins.RemoveAllAsync(repo.Session.Git));
        _shell.ShowToast(new ToastViewModel(Loc.T($"Removed {removed} ArChrono ref(s)", $"{removed} ArChrono ref'i kaldırıldı"), ToastKind.Info));
    }
}

public sealed partial class ConsoleEntryViewModel(GitCommandEventArgs entry) : ObservableObject
{
    public string Time => entry.Timestamp.ToString("HH:mm:ss");
    public string Command => entry.Command.DisplayText;
    public string Duration => entry.Result is { } r ? $"{r.Duration.TotalMilliseconds:0} ms" : "";
    public bool Failed => entry.Result is { Success: false };
    public string ExitText => entry.Result is { } r ? (r.Success ? "ok" : $"exit {r.ExitCode}") : "";
    public string Output
    {
        get
        {
            var text = entry.Result?.CombinedOutput ?? "";
            text = SecretMasker.Mask(text);
            return text.Length > 4000 ? text[..4000] + "\n…" : text.TrimEnd();
        }
    }

    public bool HasOutput => Output.Length > 0;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>Git Console: uygulamanın çalıştırdığı komutlar (şeffaflık) + serbest git komutu (recovery point ile).</summary>
public sealed partial class ConsoleViewModel : ViewModelBase
{
    private readonly RepositoryViewModel _owner;

    public ConsoleViewModel(RepositoryViewModel owner)
    {
        _owner = owner;
        foreach (var entry in owner.Shell.Services.Console.Entries.Where(e => e.Command.WorkingDirectory == owner.RootPath).TakeLast(200))
            Entries.Add(new ConsoleEntryViewModel(entry));
        owner.Shell.Services.Console.EntryAdded += OnEntryAdded;
    }

    /// <summary>Depo kapanınca veya görünüm yeniden kurulunca (dil değişimi) aboneliği bırakır.</summary>
    public void Detach() => _owner.Shell.Services.Console.EntryAdded -= OnEntryAdded;

    private void OnEntryAdded(object? sender, GitCommandEventArgs e)
    {
        if (e.Command.WorkingDirectory != _owner.RootPath) return;
        OnUi(() =>
        {
            Entries.Add(new ConsoleEntryViewModel(e));
            while (Entries.Count > 300) Entries.RemoveAt(0);
        });
    }

    public ObservableCollection<ConsoleEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    public partial string CommandText { get; set; } = string.Empty;

    [RelayCommand]
    private async Task Run()
    {
        var text = CommandText.Trim();
        if (text.StartsWith("git ", StringComparison.Ordinal)) text = text[4..];
        var args = SplitArguments(text);
        if (args.Count == 0) return;
        CommandText = string.Empty;
        var outcome = await _owner.RunAsync(() => _owner.Session.Actions.RunConsoleCommandAsync(args), "git " + text);
        if (outcome?.Execution?.Message is { Length: > 0 } output && outcome.Succeeded)
            Entries.LastOrDefault()?.ToggleCommand.Execute(null);
    }

    [RelayCommand]
    private void Clear() => Entries.Clear();

    [RelayCommand]
    private void OpenTerminal() => ShellIntegration.OpenTerminal(_owner.RootPath, _owner.Shell.Services.Settings.Current.TerminalApplication);

    internal static List<string> SplitArguments(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var hasToken = false;
        foreach (var c in text)
        {
            if (quote is not null)
            {
                if (c == quote) quote = null;
                else current.Append(c);
                continue;
            }
            if (c is '"' or '\'')
            {
                quote = c;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (hasToken || current.Length > 0) result.Add(current.ToString());
                current.Clear();
                hasToken = false;
            }
            else
            {
                current.Append(c);
            }
        }
        if (hasToken || current.Length > 0) result.Add(current.ToString());
        return result;
    }
}
