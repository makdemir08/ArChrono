using System.Collections.ObjectModel;
using System.Text;
using ArChrono.AI;
using ArChrono.AI.Context;
using ArChrono.AI.Providers;
using ArChrono.App.Infrastructure;
using ArChrono.App.ViewModels.Dialogs;
using ArChrono.Git.Diffing;
using ArChrono.Git.Errors;
using ArChrono.Git.Models;
using ArChrono.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArChrono.App.ViewModels;

public enum DiffRowKind
{
    Hunk,
    Context,
    Added,
    Removed,
    Marker,
}

public sealed class DiffRowViewModel(DiffRowKind kind, string text, int? oldLine, int? newLine, DiffHunk? hunk, DiffViewModel owner)
{
    public DiffRowKind Kind { get; } = kind;
    public string Text { get; } = text;
    public string OldLine { get; } = oldLine?.ToString() ?? "";
    public string NewLine { get; } = newLine?.ToString() ?? "";
    public DiffHunk? Hunk { get; } = hunk;
    public DiffViewModel Owner { get; } = owner;
    public bool IsHunk => Kind == DiffRowKind.Hunk;
    public bool IsAdded => Kind == DiffRowKind.Added;
    public bool IsRemoved => Kind == DiffRowKind.Removed;
    public bool IsMarker => Kind == DiffRowKind.Marker;
    public bool CanRunHunkAction => IsHunk && Owner.HunkActionLabel is not null;
    public string Prefix => Kind switch { DiffRowKind.Added => "+", DiffRowKind.Removed => "−", _ => " " };

    public string BackgroundKey => Kind switch
    {
        DiffRowKind.Added => "Chrono.DiffAddedBackground",
        DiffRowKind.Removed => "Chrono.DiffRemovedBackground",
        DiffRowKind.Hunk => "Chrono.DiffHunkBackground",
        _ => "Chrono.Panel",
    };

    public string ForegroundKey => Kind switch
    {
        DiffRowKind.Added => "Chrono.DiffAddedText",
        DiffRowKind.Removed => "Chrono.DiffRemovedText",
        DiffRowKind.Hunk => "Chrono.DiffHunkText",
        DiffRowKind.Marker => "Chrono.TextTertiary",
        _ => "Chrono.TextPrimary",
    };
}

/// <summary>Satır tabanlı diff görünümü (sanallaştırılmış). İsteğe bağlı hunk stage/unstage eylemi.</summary>
public sealed partial class DiffViewModel : ViewModelBase
{
    private readonly Func<FileDiff, DiffHunk, Task>? _hunkAction;

    public DiffViewModel(FileDiff diff, string? hunkActionLabel = null, Func<FileDiff, DiffHunk, Task>? hunkAction = null)
    {
        File = diff;
        HunkActionLabel = hunkAction is null ? null : hunkActionLabel;
        _hunkAction = hunkAction;
        var rows = new List<DiffRowViewModel>();
        foreach (var hunk in diff.Hunks)
        {
            rows.Add(new DiffRowViewModel(DiffRowKind.Hunk, hunk.HeaderLine, null, null, hunk, this));
            foreach (var line in hunk.Lines)
            {
                var kind = line.Kind switch
                {
                    DiffLineKind.Added => DiffRowKind.Added,
                    DiffLineKind.Removed => DiffRowKind.Removed,
                    DiffLineKind.NoNewlineMarker => DiffRowKind.Marker,
                    _ => DiffRowKind.Context,
                };
                rows.Add(new DiffRowViewModel(kind, line.Text.Replace("\t", "    "), line.OldLineNumber, line.NewLineNumber, null, this));
            }
        }
        Rows = rows;
    }

    public FileDiff File { get; }
    public IReadOnlyList<DiffRowViewModel> Rows { get; }
    public string? HunkActionLabel { get; }
    public string Path => File.Path;
    public string Stats => File.IsBinary ? Loc.T("binary", "ikili") : $"+{File.Additions}  −{File.Deletions}";
    public bool IsBinary => File.IsBinary;
    public bool IsEmpty => !File.IsBinary && File.Hunks.Count == 0;
    public string EmptyText => File.ModeChanged ? Loc.T("Only the file mode changed.", "Yalnızca dosya modu değişti.") : File.Change == ChangeKind.Renamed ? Loc.T("Renamed without content changes.", "İçerik değişmeden yeniden adlandırıldı.") : Loc.T("No text changes.", "Metin değişikliği yok.");
    public bool IsTruncated => File.IsTruncated;

    [RelayCommand]
    private async Task RunHunkAction(DiffRowViewModel row)
    {
        if (_hunkAction is not null && row.Hunk is not null) await _hunkAction(File, row.Hunk);
    }

    /// <summary>Tek bir hunk'tan git apply için patch üretir.</summary>
    public static string BuildPatch(FileDiff file, DiffHunk hunk)
    {
        var path = file.NewPath ?? file.OldPath!;
        var builder = new StringBuilder();
        builder.Append("diff --git a/").Append(file.OldPath ?? path).Append(" b/").Append(path).Append('\n');
        builder.Append("--- a/").Append(file.OldPath ?? path).Append('\n');
        builder.Append("+++ b/").Append(path).Append('\n');
        builder.Append($"@@ -{hunk.OldStart},{hunk.OldCount} +{hunk.NewStart},{hunk.NewCount} @@\n");
        foreach (var line in hunk.Lines)
        {
            builder.Append(line.Kind switch
            {
                DiffLineKind.Added => "+",
                DiffLineKind.Removed => "-",
                DiffLineKind.NoNewlineMarker => "\\",
                _ => " ",
            });
            builder.Append(line.Kind == DiffLineKind.NoNewlineMarker ? " " + line.Text : line.Text).Append('\n');
        }
        return builder.ToString();
    }
}

public enum AiState
{
    NotConfigured,
    Idle,
    Loading,
    Ready,
    Error,
}

/// <summary>AI açıklaması: önizleme → gönderme → "AI generated" rozetli sonuç. Kaynak commit'ler her zaman gösterilir.</summary>
public sealed partial class AiExplanationViewModel : ObservableObject
{
    private readonly RepositoryViewModel _owner;
    private readonly Func<CancellationToken, Task<AiPayload>> _prepare;
    private CancellationTokenSource? _cancellation;

    public AiExplanationViewModel(RepositoryViewModel owner, string sourceDescription, Func<CancellationToken, Task<AiPayload>> prepare)
    {
        _owner = owner;
        _prepare = prepare;
        SourceDescription = sourceDescription;
        State = owner.Shell.Services.Ai.IsReady ? AiState.Idle : AiState.NotConfigured;
    }

    public string SourceDescription { get; }
    public string NotConfiguredText => _owner.Shell.Services.Ai.NotReadyReason;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsLoading), nameof(IsReady), nameof(IsError), nameof(IsNotConfigured))]
    public partial AiState State { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Meta { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ErrorText { get; set; } = string.Empty;

    public bool IsIdle => State == AiState.Idle;
    public bool IsLoading => State == AiState.Loading;
    public bool IsReady => State == AiState.Ready;
    public bool IsError => State == AiState.Error;
    public bool IsNotConfigured => State == AiState.NotConfigured;

    public void TryLoadCached()
    {
        if (State != AiState.Idle) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var payload = await _prepare(CancellationToken.None);
                if (_owner.Shell.Services.Ai.TryGetCached(_owner.Session, payload) is { } cached)
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => Show(cached));
            }
            catch (Exception ex) when (ex is GitException or AiProviderException)
            {
            }
        });
    }

    [RelayCommand]
    private async Task Explain()
    {
        var ai = _owner.Shell.Services.Ai;
        if (!ai.IsReady)
        {
            State = AiState.NotConfigured;
            return;
        }

        _cancellation?.Cancel();
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        State = AiState.Loading;
        try
        {
            var payload = await Task.Run(() => _prepare(token), token);
            if (ai.Settings.AlwaysPreview)
            {
                var provider = ai.Settings.Provider;
                var preview = new AiPreviewDialogViewModel(payload, provider.Kind.ToString(), new Uri(provider.EffectiveEndpoint).Host, provider.IsLocal);
                var decision = await _owner.Shell.ShowDialogAsync(preview);
                if (decision is null)
                {
                    State = AiState.Idle;
                    return;
                }
                if (preview.DontAskAgain) _owner.Shell.Services.Settings.Update(s => s with { Ai = s.Ai with { AlwaysPreview = false } });
            }
            var result = await Task.Run(() => ai.SendAsync(_owner.Session, payload, token), token);
            Show(result);
        }
        catch (OperationCanceledException)
        {
            State = AiState.Idle;
        }
        catch (Exception ex) when (ex is AiProviderException or GitException or Platform.Credentials.CredentialStoreException)
        {
            ErrorText = ex is GitException git ? git.Error.Title : ex.Message;
            State = AiState.Error;
        }
    }

    private void Show(AiResult result)
    {
        Text = result.Text;
        Meta = $"{Loc.T(AiResult.Badge, "AI üretimi")} · {result.Provider} · {result.Model}" + (result.FromCache ? Loc.T(" · cached", " · önbellekten") : $" · {result.Duration.TotalSeconds:0.0}s");
        State = AiState.Ready;
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand]
    private Task OpenSettings() => _owner.Shell.OpenSettings();

    [RelayCommand]
    private Task Copy() => _owner.Shell.CopyToClipboardAsync(Text);
}

public sealed record CommitFileItem(CommitFileChange Change)
{
    public string Path => Change.Path;
    public string FileName => System.IO.Path.GetFileName(Change.Path);
    public string Directory => System.IO.Path.GetDirectoryName(Change.Path)?.Replace('\\', '/') ?? "";
    public string Letter => Format.ChangeLetter(Change.Change);
    public string BrushKey => Format.ChangeResource(Change.Change);
    public string Stats => Change.IsBinary ? Loc.T("bin", "ikili") : $"+{Change.Additions} −{Change.Deletions}";
}

public sealed record FileHistoryItem(FileHistoryEntry Entry)
{
    public string Subject => Entry.Commit.Subject;
    public string Meta => $"{Entry.Commit.ShortSha} · {Entry.Commit.AuthorName} · {Format.Relative(Entry.Commit.CommitDate)}" + (Entry.OldPath is not null ? Loc.T($" · renamed from {Entry.OldPath}", $" · önceki adı {Entry.OldPath}") : "");
}

public enum CommitTab
{
    Changes,
    Explanation,
    Info,
    History,
}

/// <summary>Seçili commit: değişen dosyalar + diff, AI açıklaması, gerçek Git metadata'sı, dosya geçmişi.</summary>
public sealed partial class CommitDetailsViewModel : ViewModelBase
{
    private readonly RepositoryViewModel _owner;

    public CommitDetailsViewModel(RepositoryViewModel owner, CommitInfo commit)
    {
        _owner = owner;
        Commit = commit;
        Explanation = new AiExplanationViewModel(owner, Loc.T($"Source: commit {commit.ShortSha} ({commit.Subject})", $"Kaynak: {commit.ShortSha} commit'i ({commit.Subject})"),
            token => owner.Shell.Services.Ai.PrepareExplainCommitAsync(owner.Session, commit.Sha, token));
    }

    public CommitInfo Commit { get; }
    public AiExplanationViewModel Explanation { get; }
    public ObservableCollection<CommitFileItem> Files { get; } = [];
    public ObservableCollection<FileHistoryItem> History { get; } = [];
    public string Subject => Commit.Subject;
    public string FullSha => Commit.Sha;
    public string AuthorLine => $"{Commit.AuthorName} <{Commit.AuthorEmail}>";
    public string DateLine => Commit.AuthorDate.ToString("yyyy-MM-dd HH:mm") + (Commit.CommitDate != Commit.AuthorDate ? Loc.T($" (committed {Commit.CommitDate:yyyy-MM-dd HH:mm} by {Commit.CommitterName})", $" ({Commit.CommitterName} tarafından {Commit.CommitDate:yyyy-MM-dd HH:mm} tarihinde commit edildi)") : "");
    public string ParentsLine => Commit.Parents.Count == 0 ? Loc.T("none (first commit)", "yok (ilk commit)") : string.Join(", ", Commit.Parents.Select(p => p[..7]));

    [ObservableProperty]
    public partial string Body { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChangesTab), nameof(IsExplanationTab), nameof(IsInfoTab), nameof(IsHistoryTab))]
    public partial CommitTab Tab { get; set; }

    [ObservableProperty]
    public partial CommitFileItem? SelectedFile { get; set; }

    [ObservableProperty]
    public partial DiffViewModel? Diff { get; set; }

    [ObservableProperty]
    public partial FileHistoryItem? SelectedHistory { get; set; }

    public bool IsChangesTab { get => Tab == CommitTab.Changes; set { if (value) Tab = CommitTab.Changes; } }
    public bool IsExplanationTab { get => Tab == CommitTab.Explanation; set { if (value) Tab = CommitTab.Explanation; } }
    public bool IsInfoTab { get => Tab == CommitTab.Info; set { if (value) Tab = CommitTab.Info; } }
    public bool IsHistoryTab { get => Tab == CommitTab.History; set { if (value) Tab = CommitTab.History; } }

    public async Task LoadAsync()
    {
        try
        {
            var details = await Task.Run(() => _owner.Session.Git.History.GetCommitDetailsAsync(Commit.Sha));
            Body = details.Body;
            foreach (var file in details.Files) Files.Add(new CommitFileItem(file));
            Summary = Loc.T($"{details.Files.Count} file(s) changed · +{details.TotalAdditions} −{details.TotalDeletions}", $"{details.Files.Count} dosya değişti · +{details.TotalAdditions} −{details.TotalDeletions}");
            SelectedFile = Files.FirstOrDefault();
            Explanation.TryLoadCached();
        }
        catch (GitException ex)
        {
            Summary = ex.Error.Title;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void ShowExplanation()
    {
        Tab = CommitTab.Explanation;
        if (Explanation.IsIdle) Explanation.ExplainCommand.Execute(null);
    }

    partial void OnSelectedFileChanged(CommitFileItem? value)
    {
        History.Clear();
        if (value is null)
        {
            Diff = null;
            return;
        }
        _ = LoadDiffAsync(value);
        if (Tab == CommitTab.History) _ = LoadHistoryAsync(value.Path);
    }

    partial void OnTabChanged(CommitTab value)
    {
        if (value == CommitTab.History && SelectedFile is not null && History.Count == 0) _ = LoadHistoryAsync(SelectedFile.Path);
    }

    private async Task LoadDiffAsync(CommitFileItem file)
    {
        try
        {
            var diffs = await Task.Run(() => _owner.Session.Git.Diff.GetCommitDiffAsync(Commit, file.Path));
            if (SelectedFile == file) Diff = new DiffViewModel(diffs.FirstOrDefault() ?? new FileDiff(file.Path, file.Path, file.Change.Change, file.Change.IsBinary, null, null, []));
        }
        catch (GitException ex)
        {
            _owner.Shell.ShowToast(new ToastViewModel(Loc.T("Could not load the diff.", "Diff yüklenemedi."), ToastKind.Warning, ex.Error.Title));
        }
    }

    private async Task LoadHistoryAsync(string path)
    {
        var entries = await Task.Run(() => _owner.Session.Git.History.GetFileHistoryAsync(path));
        History.Clear();
        foreach (var entry in entries) History.Add(new FileHistoryItem(entry));
    }

    partial void OnSelectedHistoryChanged(FileHistoryItem? value)
    {
        if (value is null) return;
        _owner.Workspace.RevealCommit(value.Entry.Commit.Sha);
    }

    [RelayCommand] private Task CopySha() => _owner.Shell.CopyToClipboardAsync(Commit.Sha);
    [RelayCommand] private Task Checkout() => _owner.CheckoutCommitAsync(Commit);
    [RelayCommand] private Task Reset() => _owner.ResetToAsync(Commit);
    [RelayCommand] private Task Revert() => _owner.RevertAsync(Commit);
    [RelayCommand] private Task CherryPick() => _owner.CherryPickAsync(Commit);
    [RelayCommand] private Task CreateBranch() => _owner.CreateBranch(Commit.Sha);
}

public sealed partial class FileChangeItem(StatusEntry entry, bool staged) : ObservableObject
{
    public StatusEntry Entry { get; } = entry;
    public bool IsStaged { get; } = staged;
    public string Path => Entry.Path;
    public string FileName => System.IO.Path.GetFileName(Entry.Path);
    public string Directory => System.IO.Path.GetDirectoryName(Entry.Path)?.Replace('\\', '/') ?? "";
    private ChangeKind Kind => IsStaged ? Entry.IndexChange : Entry.DisplayChange;
    public string Letter => Format.ChangeLetter(Kind);
    public string BrushKey => Format.ChangeResource(Kind);
    public string ToolTip => Entry.OriginalPath is null ? Entry.Path : $"{Entry.OriginalPath} → {Entry.Path}";
}

/// <summary>Commit composer: unstaged / staged, diff, mesaj, AI ile mesaj üretimi.</summary>
public sealed partial class ChangesViewModel : ViewModelBase
{
    private readonly RepositoryViewModel _owner;

    public ChangesViewModel(RepositoryViewModel owner)
    {
        _owner = owner;
    }

    public ObservableCollection<FileChangeItem> Unstaged { get; } = [];
    public ObservableCollection<FileChangeItem> Staged { get; } = [];

    public string Header => _owner.IsPro ? "Commit" : Loc.T("Save my current work", "Mevcut çalışmamı kaydet");
    public string UnstagedHeader => _owner.IsPro ? Loc.T($"UNSTAGED ({Unstaged.Count})", $"STAGE EDİLMEMİŞ ({Unstaged.Count})") : Loc.T($"CHANGED FILES ({Unstaged.Count})", $"DEĞİŞEN DOSYALAR ({Unstaged.Count})");
    public string StagedHeader => _owner.IsPro ? Loc.T($"STAGED ({Staged.Count})", $"STAGE EDİLMİŞ ({Staged.Count})") : Loc.T($"WILL BE SAVED ({Staged.Count})", $"KAYDEDİLECEK ({Staged.Count})");
    public string CommitButtonText => IsAmend ? "Amend" : _owner.IsPro ? Loc.T($"Commit {Staged.Count} file(s)", $"{Staged.Count} dosyayı commit et") : Loc.T($"Save {Staged.Count} file(s)", $"{Staged.Count} dosyayı kaydet");
    public string MessageWatermark => _owner.IsPro ? Loc.T("Commit message", "Commit mesajı") : Loc.T("Describe what you changed…", "Neyi değiştirdiğinizi anlatın…");
    public bool AiReady => _owner.Shell.Services.Ai.IsReady;
    public bool HasChanges => Unstaged.Count + Staged.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    public partial string Message { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommitButtonText))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    public partial bool IsAmend { get; set; }

    [ObservableProperty]
    public partial FileChangeItem? SelectedFile { get; set; }

    [ObservableProperty]
    public partial DiffViewModel? Diff { get; set; }

    [ObservableProperty]
    public partial bool IsGenerating { get; set; }

    [ObservableProperty]
    public partial CommitMessageStyle Style { get; set; }

    [ObservableProperty]
    public partial string AiNote { get; set; } = string.Empty;

    public void OnModeChanged()
    {
        foreach (var name in new[] { nameof(Header), nameof(UnstagedHeader), nameof(StagedHeader), nameof(CommitButtonText), nameof(MessageWatermark) })
            OnPropertyChanged(name);
    }

    public void Update(RepositoryStatus status)
    {
        var selectedPath = SelectedFile?.Path;
        var selectedStaged = SelectedFile?.IsStaged ?? false;

        Sync(Unstaged, status.Entries.Where(e => e.HasWorktreeChanges).Select(e => new FileChangeItem(e, false)).ToList());
        Sync(Staged, status.Entries.Where(e => e.HasStagedChanges).Select(e => new FileChangeItem(e, true)).ToList());

        var reselect = (selectedStaged ? Staged : Unstaged).FirstOrDefault(f => f.Path == selectedPath)
                       ?? (selectedStaged ? Unstaged : Staged).FirstOrDefault(f => f.Path == selectedPath);
        if (reselect is not null)
        {
            if (SelectedFile is null || SelectedFile.Path != reselect.Path || SelectedFile.IsStaged != reselect.IsStaged || SelectedFile.Entry != reselect.Entry)
                SelectedFile = reselect;
            else
                _ = LoadDiffAsync(reselect);
        }
        else
        {
            SelectedFile = Unstaged.FirstOrDefault() ?? Staged.FirstOrDefault();
        }

        foreach (var name in new[] { nameof(UnstagedHeader), nameof(StagedHeader), nameof(CommitButtonText), nameof(HasChanges), nameof(AiReady) })
            OnPropertyChanged(name);
        CommitCommand.NotifyCanExecuteChanged();
    }

    private static void Sync(ObservableCollection<FileChangeItem> target, List<FileChangeItem> items)
    {
        if (target.Count == items.Count && target.Zip(items).All(p => p.First.Entry == p.Second.Entry)) return;
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    partial void OnSelectedFileChanged(FileChangeItem? value)
    {
        if (value is null) Diff = null;
        else _ = LoadDiffAsync(value);
    }

    partial void OnIsAmendChanged(bool value)
    {
        if (value && string.IsNullOrWhiteSpace(Message))
            _ = Task.Run(() => _owner.Session.Git.WorkingTree.GetHeadMessageAsync()).ContinueWith(t => OnUi(() => Message = t.Result), TaskScheduler.Default);
    }

    private async Task LoadDiffAsync(FileChangeItem item)
    {
        try
        {
            var git = _owner.Session.Git;
            FileDiff diff;
            if (item.Entry.IsUntracked) diff = await Task.Run(() => git.Diff.GetUntrackedFileDiff(item.Path));
            else
            {
                var diffs = item.IsStaged
                    ? await Task.Run(() => git.Diff.GetStagedAsync(item.Path))
                    : await Task.Run(() => git.Diff.GetUnstagedAsync(item.Path));
                diff = diffs.FirstOrDefault() ?? new FileDiff(item.Path, item.Path, item.Entry.DisplayChange, false, null, null, []);
            }
            if (SelectedFile != item) return;

            var canStageHunks = !item.Entry.IsUntracked && !item.Entry.IsConflicted && diff.Change == ChangeKind.Modified;
            Diff = canStageHunks
                ? new DiffViewModel(diff, item.IsStaged ? Loc.T("Unstage hunk", "Hunk'ı unstage et") : Loc.T("Stage hunk", "Hunk'ı stage et"), (file, hunk) => ApplyHunkAsync(file, hunk, item.IsStaged))
                : new DiffViewModel(diff);
        }
        catch (GitException ex)
        {
            _owner.Shell.ShowToast(new ToastViewModel(Loc.T("Could not load the diff.", "Diff yüklenemedi."), ToastKind.Warning, ex.Error.Title));
        }
    }

    private async Task ApplyHunkAsync(FileDiff file, DiffHunk hunk, bool unstage)
    {
        try
        {
            await Task.Run(() => _owner.Session.Actions.ApplyPatchToIndexAsync(DiffViewModel.BuildPatch(file, hunk), reverse: unstage));
        }
        catch (GitException ex)
        {
            await _owner.Shell.ShowErrorAsync(ex.Error);
        }
    }

    [RelayCommand]
    private Task Stage(FileChangeItem item) => SafeAsync(() => _owner.Session.Actions.StageAsync([item.Path]));

    [RelayCommand]
    private Task Unstage(FileChangeItem item) => SafeAsync(() => _owner.Session.Actions.UnstageAsync([item.Path]));

    [RelayCommand]
    private Task StageAll() => SafeAsync(() => _owner.Session.Actions.StageAllAsync());

    [RelayCommand]
    private Task UnstageAll() => SafeAsync(() => _owner.Session.Actions.UnstageAllAsync());

    [RelayCommand]
    private Task Discard(FileChangeItem item) => _owner.DiscardAsync([item.Entry]);

    [RelayCommand]
    private Task DiscardAll() => Unstaged.Count == 0 ? Task.CompletedTask : _owner.DiscardAsync(Unstaged.Select(u => u.Entry).ToList());

    [RelayCommand]
    private Task Stash() => _owner.StashCommand.ExecuteAsync(null);

    private bool CanCommit() => !string.IsNullOrWhiteSpace(Message) && (Staged.Count > 0 || IsAmend || _owner.State == RepositoryState.Merging);

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task Commit()
    {
        var outcome = await _owner.CommitAsync(Message.Trim(), IsAmend);
        if (outcome?.Succeeded == true)
        {
            Message = string.Empty;
            IsAmend = false;
            AiNote = string.Empty;
        }
    }

    [RelayCommand]
    private async Task GenerateMessage()
    {
        var ai = _owner.Shell.Services.Ai;
        if (!ai.IsReady)
        {
            _owner.Shell.ShowToast(new ToastViewModel(ai.NotReadyReason, ToastKind.Info, null, new ToastAction(Loc.T("Open settings", "Ayarları aç"), () => _owner.Shell.OpenSettings())));
            return;
        }
        if (Staged.Count == 0)
        {
            _owner.Shell.ShowToast(new ToastViewModel(Loc.T("Stage the files you want to commit first.", "Önce commit etmek istediğiniz dosyaları stage edin."), ToastKind.Info));
            return;
        }

        IsGenerating = true;
        try
        {
            var payload = await Task.Run(() => ai.PrepareCommitMessageAsync(_owner.Session, ai.Settings.CommitStyle));
            if (ai.Settings.AlwaysPreview)
            {
                var provider = ai.Settings.Provider;
                var preview = new AiPreviewDialogViewModel(payload, provider.Kind.ToString(), new Uri(provider.EffectiveEndpoint).Host, provider.IsLocal);
                if (await _owner.Shell.ShowDialogAsync(preview) is null) return;
                if (preview.DontAskAgain) _owner.Shell.Services.Settings.Update(s => s with { Ai = s.Ai with { AlwaysPreview = false } });
            }
            var result = await Task.Run(() => ai.SendAsync(_owner.Session, payload));
            Message = result.Text;
            AiNote = Loc.T($"{AiResult.Badge} · {result.Model} — review before committing", $"{Loc.T(AiResult.Badge, "AI üretimi")} · {result.Model} — commit etmeden önce gözden geçirin");
        }
        catch (Exception ex) when (ex is AiProviderException or GitException or Platform.Credentials.CredentialStoreException)
        {
            _owner.Shell.ShowToast(new ToastViewModel(Loc.T("Could not generate a commit message.", "Commit mesajı oluşturulamadı."), ToastKind.Warning, ex is GitException g ? g.Error.Title : ex.Message));
        }
        finally
        {
            IsGenerating = false;
        }
    }

    private async Task SafeAsync(Func<Task> action)
    {
        try
        {
            await Task.Run(action);
        }
        catch (GitException ex)
        {
            await _owner.Shell.ShowErrorAsync(ex.Error);
        }
    }
}

public sealed record ConflictItem(ConflictFile File)
{
    public string Path => File.Path;
    public string Kind => File.Kind;
}

/// <summary>Conflict çözümü: Base / Current / Incoming / Result (+ AI açıklaması; AI sonucu asla otomatik uygulanmaz).</summary>
public sealed partial class ConflictResolverViewModel : ViewModelBase
{
    private readonly RepositoryViewModel _owner;

    public ConflictResolverViewModel(RepositoryViewModel owner)
    {
        _owner = owner;
        _ = LoadAsync();
    }

    public RepositoryViewModel Owner => _owner;
    public ObservableCollection<ConflictItem> Files { get; } = [];
    public string Title => _owner.StateTitle;

    [ObservableProperty]
    public partial ConflictItem? Selected { get; set; }

    [ObservableProperty]
    public partial string BaseText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IncomingText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ResultText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial AiExplanationViewModel? Explanation { get; set; }

    [ObservableProperty]
    public partial bool AllResolved { get; set; }

    private async Task LoadAsync()
    {
        var conflicts = await Task.Run(() => _owner.Session.Git.Conflicts.GetConflictsAsync());
        Files.Clear();
        foreach (var conflict in conflicts) Files.Add(new ConflictItem(conflict));
        AllResolved = Files.Count == 0;
        Selected = Files.FirstOrDefault();
    }

    partial void OnSelectedChanged(ConflictItem? value)
    {
        if (value is null) return;
        Explanation = new AiExplanationViewModel(_owner, Loc.T($"Source: {value.Path} (base, current and incoming versions)", $"Kaynak: {value.Path} (ortak, mevcut ve gelen sürümler)"),
            token => _owner.Shell.Services.Ai.PrepareExplainConflictAsync(_owner.Session, value.Path, token));
        _ = LoadFileAsync(value);
    }

    private async Task LoadFileAsync(ConflictItem item)
    {
        var git = _owner.Session.Git;
        static string Text(byte[]? bytes) => bytes is null ? Loc.T("(does not exist on this side)", "(bu tarafta mevcut değil)") : TextDiff.LooksBinary(bytes) ? Loc.T("(binary file)", "(ikili dosya)") : TextDiff.Decode(bytes);
        var (b, c, i) = await Task.Run(async () => (
            await git.Conflicts.ReadStageAsync(item.Path, 1),
            await git.Conflicts.ReadStageAsync(item.Path, 2),
            await git.Conflicts.ReadStageAsync(item.Path, 3)));
        BaseText = Text(b);
        CurrentText = Text(c);
        IncomingText = Text(i);
        var full = Path.Combine(_owner.RootPath, item.Path);
        ResultText = File.Exists(full) ? await File.ReadAllTextAsync(full) : string.Empty;
    }

    [RelayCommand]
    private async Task UseCurrent()
    {
        if (Selected is null) return;
        await _owner.RunAsync(() => _owner.Session.Actions.ResolveConflictAsync(Selected.File, useOurs: true), Loc.T("Resolving…", "Çözülüyor…"));
        await LoadAsync();
    }

    [RelayCommand]
    private async Task UseIncoming()
    {
        if (Selected is null) return;
        await _owner.RunAsync(() => _owner.Session.Actions.ResolveConflictAsync(Selected.File, useOurs: false), Loc.T("Resolving…", "Çözülüyor…"));
        await LoadAsync();
    }

    [RelayCommand]
    private async Task SaveResult()
    {
        if (Selected is null) return;
        if (ResultText.Contains("<<<<<<<", StringComparison.Ordinal) || ResultText.Contains(">>>>>>>", StringComparison.Ordinal))
        {
            _owner.Shell.ShowToast(new ToastViewModel(Loc.T("The result still contains conflict markers (<<<<<<< / >>>>>>>).", "Sonuç hâlâ çakışma işaretleri içeriyor (<<<<<<< / >>>>>>>)."), ToastKind.Warning));
            return;
        }
        var path = Selected.Path;
        var content = System.Text.Encoding.UTF8.GetBytes(ResultText);
        await _owner.RunAsync(() => _owner.Session.Actions.SaveConflictResolutionAsync(path, content), Loc.T("Saving resolution…", "Çözüm kaydediliyor…"),
            successMessage: Loc.T($"Marked {path} as resolved", $"{path} çözüldü olarak işaretlendi"));
        await LoadAsync();
    }
}
