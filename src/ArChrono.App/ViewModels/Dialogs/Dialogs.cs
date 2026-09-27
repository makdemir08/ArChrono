using System.Collections.ObjectModel;
using ArChrono.AI.Context;
using ArChrono.App.Infrastructure;
using ArChrono.Git.Errors;
using ArChrono.Git.Models;
using ArChrono.Git.Services;
using ArChrono.Localization;
using ArChrono.Recovery.Restore;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArChrono.App.ViewModels.Dialogs;

public sealed record Confirmation(bool Accepted);

/// <summary>
/// Tehlikeli işlem onayı:
/// "You are about to perform: RESET --HARD · Recovery point: Today 13:42:18".
/// </summary>
public sealed partial class ConfirmOperationDialogViewModel : DialogViewModel<Confirmation>
{
    public ConfirmOperationDialogViewModel(string operation, string guidedTitle, IReadOnlyList<string> effects, string commandPreview, bool isPro,
        bool createsRecoveryPoint = true, string? confirmText = null, bool isDanger = true)
    {
        Operation = operation;
        GuidedTitle = guidedTitle;
        Effects = effects;
        CommandPreview = commandPreview;
        IsPro = isPro;
        CreatesRecoveryPoint = createsRecoveryPoint;
        ConfirmText = confirmText ?? Loc.T("Continue", "Devam et");
        IsDanger = isDanger;
    }

    public override string Title => IsPro ? Loc.T("You are about to perform", "Şu işlemi yapmak üzeresiniz") : GuidedTitle;
    public string Operation { get; }
    public string GuidedTitle { get; }
    public IReadOnlyList<string> Effects { get; }
    public string CommandPreview { get; }
    public bool IsPro { get; }
    public bool CreatesRecoveryPoint { get; }
    public string ConfirmText { get; }
    public bool IsDanger { get; }
    public string RecoveryTime => Loc.T($"Today {DateTimeOffset.Now:HH:mm:ss}", $"Bugün {DateTimeOffset.Now:HH:mm:ss}");

    [RelayCommand]
    private void Confirm() => Close(new Confirmation(true));
}

public sealed record TextInputResult(string Text, bool Option);

public sealed partial class TextInputDialogViewModel : DialogViewModel<TextInputResult>
{
    private readonly Func<string, Task<string?>>? _validate;

    public TextInputDialogViewModel(string title, string message, string label, string initialText = "", string? confirmText = null,
        string? optionText = null, bool optionDefault = false, Func<string, Task<string?>>? validate = null, bool multiline = false)
    {
        DialogTitle = title;
        Message = message;
        Label = label;
        Text = initialText;
        ConfirmText = confirmText ?? Loc.T("OK", "Tamam");
        OptionText = optionText;
        Option = optionDefault;
        IsMultiline = multiline;
        _validate = validate;
    }

    public override string Title => DialogTitle;
    public string DialogTitle { get; }
    public string Message { get; }
    public string Label { get; }
    public string ConfirmText { get; }
    public string? OptionText { get; }
    public bool HasOption => OptionText is not null;
    public bool IsMultiline { get; }

    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    public partial bool Option { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [RelayCommand]
    private async Task Confirm()
    {
        var text = Text.Trim();
        if (_validate is not null && await _validate(text) is { } problem)
        {
            Error = problem;
            return;
        }
        Close(new TextInputResult(text, Option));
    }
}

public sealed record ErrorDialogResult(bool Undo);

public sealed partial class ErrorDialogViewModel(GitError error, bool canUndo) : DialogViewModel<ErrorDialogResult>
{
    public override string Title => error.Title;
    public string Explanation => error.Explanation;
    public string? Suggestion => error.Suggestion;
    public bool HasSuggestion => !string.IsNullOrWhiteSpace(error.Suggestion);
    public string TechnicalDetails => error.TechnicalDetails;
    public bool HasTechnicalDetails => !string.IsNullOrWhiteSpace(error.RawOutput) || !string.IsNullOrWhiteSpace(error.CommandLine);
    public bool CanUndo { get; } = canUndo;

    [ObservableProperty]
    public partial bool ShowDetails { get; set; }

    [RelayCommand]
    private void ToggleDetails() => ShowDetails = !ShowDetails;

    [RelayCommand]
    private void Undo() => Close(new ErrorDialogResult(true));

    [RelayCommand]
    private void Ok() => Close(new ErrorDialogResult(false));
}

public sealed partial class RestoreActionItem(RestoreAction action, bool isPro) : ObservableObject
{
    public RestoreAction Action { get; } = action;
    public string Description => Action.Description;
    public string CommandPreview => Action.CommandPreview;
    public bool ShowCommand => isPro;
    public string? Warning => Action.Warning;
    public bool HasWarning => Action.Warning is not null;
    public bool IsRemote => Action.IsRemote;

    [ObservableProperty]
    public partial bool Selected { get; set; } = action.Selected;
}

/// <summary>Undo / Restore planı önizlemesi. Kullanıcı adımları seçebilir; uzak adımlar ek onay ister.</summary>
public sealed partial class RestorePlanDialogViewModel : DialogViewModel<RestorePlan>
{
    private readonly Func<RestoreMode, Task<RestorePlan>>? _replan;
    private readonly bool _isPro;
    private RestorePlan _plan;

    public RestorePlanDialogViewModel(RestorePlan plan, bool isPro, Func<RestoreMode, Task<RestorePlan>>? replan = null, string? confirmText = null)
    {
        _plan = plan;
        _isPro = isPro;
        _replan = replan;
        ConfirmText = confirmText ?? Loc.T("Restore", "Geri yükle");
        Load(plan);
    }

    public override string Title => _plan.Title;
    public override double DialogWidth => 640;
    public string ConfirmText { get; }
    public bool CanChangeMode => _replan is not null;
    public ObservableCollection<RestoreActionItem> Actions { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];
    public bool HasNotes => Notes.Count > 0;
    public bool HasRemote => Actions.Any(a => a.IsRemote);
    public string TargetTime => Format.Date(_plan.TargetTime, "ddd HH:mm:ss", "ddd HH:mm:ss");

    [ObservableProperty]
    public partial bool RemoteAcknowledged { get; set; }

    [ObservableProperty]
    public partial RestoreMode Mode { get; set; } = RestoreMode.Everything;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    public bool ModeEverything
    {
        get => Mode == RestoreMode.Everything;
        set { if (value) _ = ChangeModeAsync(RestoreMode.Everything); }
    }

    public bool ModeFiles
    {
        get => Mode == RestoreMode.FilesOnly;
        set { if (value) _ = ChangeModeAsync(RestoreMode.FilesOnly); }
    }

    public bool ModeBranches
    {
        get => Mode == RestoreMode.BranchesOnly;
        set { if (value) _ = ChangeModeAsync(RestoreMode.BranchesOnly); }
    }

    private async Task ChangeModeAsync(RestoreMode mode)
    {
        if (_replan is null || Mode == mode) return;
        Mode = mode;
        IsLoading = true;
        try
        {
            _plan = await _replan(mode);
            Load(_plan);
            OnPropertyChanged(nameof(Title));
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(ModeEverything));
            OnPropertyChanged(nameof(ModeFiles));
            OnPropertyChanged(nameof(ModeBranches));
        }
    }

    private void Load(RestorePlan plan)
    {
        Actions.Clear();
        foreach (var action in plan.Actions.OrderBy(a => a.Order)) Actions.Add(new RestoreActionItem(action, _isPro));
        Notes.Clear();
        foreach (var note in plan.Notes) Notes.Add(note);
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(HasRemote));
    }

    [RelayCommand]
    private void Confirm()
    {
        var plan = _plan;
        foreach (var item in Actions)
        {
            var selected = item.Selected && (!item.IsRemote || RemoteAcknowledged);
            if (selected != item.Action.Selected) plan = plan.WithSelection(item.Action, selected);
        }
        Close(plan);
    }
}

public sealed record AiPreviewResult(bool Send);

public sealed partial class AiPreviewDialogViewModel(AiPayload payload, string providerName, string endpointHost, bool isLocal) : DialogViewModel<AiPreviewResult>
{
    public override string Title => Loc.T("Review what will be sent", "Gönderilecekleri gözden geçirin");
    public override double DialogWidth => 680;
    public string Provider => providerName;
    public string Host => endpointHost;
    public bool IsLocal => isLocal;
    public string Destination => isLocal ? Loc.T($"{providerName} on this computer ({endpointHost})", $"Bu bilgisayardaki {providerName} ({endpointHost})") : $"{providerName} · {endpointHost}";
    public IReadOnlyList<string> IncludedFiles => payload.IncludedPaths;
    public IReadOnlyList<string> ExcludedFiles => payload.ExcludedPaths.Select(e => $"{e.Path} — {e.Reason}").ToList();
    public bool HasExcluded => payload.ExcludedPaths.Count > 0;
    public string Summary => Loc.T($"{payload.IncludedPaths.Count} file(s) · {payload.ByteCount / 1024.0:0.#} KB", $"{payload.IncludedPaths.Count} dosya · {Format.Bytes(payload.ByteCount)}") +
                             (payload.RedactionCount > 0 ? Loc.T($" · {payload.RedactionCount} secret(s) masked", $" · {payload.RedactionCount} gizli bilgi maskelendi") : "") +
                             (payload.TruncatedPaths.Count > 0 ? Loc.T($" · {payload.TruncatedPaths.Count} shortened", $" · {payload.TruncatedPaths.Count} kısaltıldı") : "");
    public string FullText => "SYSTEM\n" + payload.Prompt.System + "\n\nUSER\n" + payload.Prompt.User;

    [ObservableProperty]
    public partial bool ShowFullText { get; set; }

    [ObservableProperty]
    public partial bool DontAskAgain { get; set; }

    [RelayCommand]
    private void ToggleFullText() => ShowFullText = !ShowFullText;

    [RelayCommand]
    private void Send() => Close(new AiPreviewResult(true));
}

public enum IntegrationKind
{
    Merge,
    Rebase,
}

public sealed record IntegrationChoice(IntegrationKind Kind, FastForwardMode FastForward);

/// <summary>Merge/Rebase öncesi conflict tahmini (git merge-tree, çalışma alanına dokunmaz).</summary>
public sealed partial class IntegrateDialogViewModel : DialogViewModel<IntegrationChoice>
{
    public IntegrateDialogViewModel(string source, string target, bool isPro, IntegrationKind initial, Func<Task<ConflictPrediction?>> predict)
    {
        Source = source;
        Target = target;
        IsPro = isPro;
        IsRebase = initial == IntegrationKind.Rebase;
        _ = LoadAsync(predict);
    }

    public override string Title => IsPro ? (IsRebase ? Loc.T($"Rebase {Target} onto {Source}", $"{Target}, {Source} üzerine rebase") : Loc.T($"Merge {Source} into {Target}", $"{Source} → {Target} merge")) : Loc.T("Combine these changes", "Bu değişiklikleri birleştir");
    public override double DialogWidth => 600;
    public string Source { get; }
    public string Target { get; }
    public bool IsPro { get; }
    public ObservableCollection<PredictedConflict> Files { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Explanation))]
    public partial bool IsRebase { get; set; }

    [ObservableProperty]
    public partial bool NoFastForward { get; set; }

    [ObservableProperty]
    public partial bool IsPredicting { get; set; } = true;

    [ObservableProperty]
    public partial string PredictionSummary { get; set; } = Loc.T("Checking for conflicts…", "Çakışmalar kontrol ediliyor…");

    [ObservableProperty]
    public partial string PredictionKey { get; set; } = "Chrono.TextSecondary";

    public string Explanation => IsRebase
        ? Loc.T($"Your commits on {Target} will be replayed on top of {Source}. History looks linear; commit ids change.", $"{Target} üzerindeki commit'leriniz {Source} üzerine yeniden uygulanacak. Geçmiş doğrusal görünür; commit kimlikleri değişir.")
        : Loc.T($"Changes from {Source} will be combined into {Target} with a merge commit (or a fast-forward if possible).", $"{Source} değişiklikleri bir merge commit'iyle (mümkünse fast-forward ile) {Target} içine birleştirilecek.");

    private async Task LoadAsync(Func<Task<ConflictPrediction?>> predict)
    {
        try
        {
            var prediction = await predict();
            if (prediction is null)
            {
                PredictionSummary = Loc.T("Conflict prediction is not available.", "Çakışma tahmini kullanılamıyor.");
                return;
            }
            foreach (var file in prediction.Files) Files.Add(file);
            if (prediction.HasConflicts)
            {
                PredictionSummary = Loc.T($"Potential conflicts detected: {prediction.Files.Count(f => f.Severity != ConflictSeverity.AutoMerged)} file(s) — ", $"Olası çakışmalar bulundu: {prediction.Files.Count(f => f.Severity != ConflictSeverity.AutoMerged)} dosya — ") +
                                    Loc.T($"{prediction.ContentConflicts} textual, {prediction.StructuralConflicts} structural", $"{prediction.ContentConflicts} metinsel, {prediction.StructuralConflicts} yapısal") +
                                    (prediction.AutoMergedFiles > 0 ? Loc.T($", {prediction.AutoMergedFiles} merged automatically (review)", $", {prediction.AutoMergedFiles} otomatik birleştirildi (inceleyin)") : "");
                PredictionKey = "Chrono.Time";
            }
            else if (prediction.AutoMergedFiles > 0)
            {
                PredictionSummary = Loc.T($"No conflicts. {prediction.AutoMergedFiles} file(s) changed on both sides merge automatically — worth a quick review.", $"Çakışma yok. İki tarafta da değişen {prediction.AutoMergedFiles} dosya otomatik birleşiyor — hızlıca incelemeye değer.");
                PredictionKey = "Chrono.Safe";
            }
            else
            {
                PredictionSummary = Loc.T("No conflicts expected.", "Çakışma beklenmiyor.");
                PredictionKey = "Chrono.Safe";
            }
        }
        catch (GitException ex)
        {
            PredictionSummary = ex.Error.Title;
        }
        finally
        {
            IsPredicting = false;
        }
    }

    [RelayCommand]
    private void Confirm() => Close(new IntegrationChoice(IsRebase ? IntegrationKind.Rebase : IntegrationKind.Merge,
        NoFastForward ? FastForwardMode.Never : FastForwardMode.Auto));
}

public sealed record ResetChoice(ResetMode Mode);

public sealed partial class ResetDialogViewModel(CommitInfo commit, string branch, bool isPro) : DialogViewModel<ResetChoice>
{
    public override string Title => isPro ? Loc.T($"Reset {branch} to {commit.ShortSha}", $"Reset {branch} → {commit.ShortSha}") : Loc.T("Go back to an earlier commit", "Önceki bir commit'e dön");
    public string CommitSubject => commit.Subject;
    public string CommitMeta => $"{commit.ShortSha} · {commit.AuthorName} · " + Format.Date(commit.CommitDate, "MMM d, HH:mm", "d MMM, HH:mm");
    public bool IsPro => isPro;

    [ObservableProperty]
    public partial bool Soft { get; set; }

    [ObservableProperty]
    public partial bool Mixed { get; set; } = true;

    [ObservableProperty]
    public partial bool Hard { get; set; }

    [RelayCommand]
    private void Confirm() => Close(new ResetChoice(Hard ? ResetMode.Hard : Soft ? ResetMode.Soft : ResetMode.Mixed));
}

public sealed record CloneRequest(string Url, string Destination);

public sealed partial class CloneDialogViewModel(IShell shell) : DialogViewModel<CloneRequest>
{
    public override string Title => Loc.T("Clone a repository", "Bir depoyu klonla");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Destination))]
    public partial string Url { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Destination))]
    public partial string ParentFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Source");

    [ObservableProperty]
    public partial string? Error { get; set; }

    public string Destination
    {
        get
        {
            var name = Url.TrimEnd('/').Split('/', ':').LastOrDefault() ?? "";
            if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            return string.IsNullOrWhiteSpace(name) ? ParentFolder : Path.Combine(ParentFolder, name);
        }
    }

    [RelayCommand]
    private async Task Browse()
    {
        if (await shell.PickFolderAsync(Loc.T("Choose where to clone", "Nereye klonlanacağını seçin")) is { } folder) ParentFolder = folder;
    }

    [RelayCommand]
    private void Confirm()
    {
        if (string.IsNullOrWhiteSpace(Url))
        {
            Error = Loc.T("Enter the repository URL.", "Depo URL'sini girin.");
            return;
        }
        if (Directory.Exists(Destination) && Directory.EnumerateFileSystemEntries(Destination).Any())
        {
            Error = Loc.T("The destination folder already exists and is not empty.", "Hedef klasör zaten var ve boş değil.");
            return;
        }
        Close(new CloneRequest(Url.Trim(), Destination));
    }
}

public sealed record InteractiveRebaseRequest(IReadOnlyList<RebaseTodoItem> Items);

public sealed partial class RebaseItemViewModel(CommitInfo commit) : ObservableObject
{
    public CommitInfo Commit { get; } = commit;
    public string ShortSha => Commit.ShortSha;
    public static IReadOnlyList<RebaseAction> AllActions { get; } = Enum.GetValues<RebaseAction>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDropped), nameof(IsCombined), nameof(CanEditMessage))]
    public partial RebaseAction Action { get; set; } = RebaseAction.Pick;

    [ObservableProperty]
    public partial string Message { get; set; } = commit.Subject;

    public bool IsDropped => Action == RebaseAction.Drop;
    public bool IsCombined => Action is RebaseAction.Squash or RebaseAction.Fixup;
    public bool CanEditMessage => Action is RebaseAction.Reword or RebaseAction.Squash;
}

/// <summary>Görsel interactive rebase: sırala (sürükle / Alt+↑↓), pick/reword/edit/squash/fixup/drop.</summary>
public sealed partial class InteractiveRebaseDialogViewModel : DialogViewModel<InteractiveRebaseRequest>
{
    public InteractiveRebaseDialogViewModel(IReadOnlyList<CommitInfo> commitsOldestFirst, string branch)
    {
        Branch = branch;
        foreach (var commit in commitsOldestFirst) Items.Add(new RebaseItemViewModel(commit));
    }

    public override string Title => Loc.T($"Edit history of {Branch}", $"{Branch} geçmişini düzenle");
    public override double DialogWidth => 720;
    public string Branch { get; }
    public ObservableCollection<RebaseItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand), nameof(MoveDownCommand))]
    public partial RebaseItemViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(1);

    private bool CanMoveUp() => Selected is not null && Items.IndexOf(Selected) > 0;

    private bool CanMoveDown() => Selected is not null && Items.IndexOf(Selected) < Items.Count - 1;

    public void Move(int delta)
    {
        if (Selected is not { } item) return;
        var index = Items.IndexOf(item);
        var target = Math.Clamp(index + delta, 0, Items.Count - 1);
        if (target == index) return;
        Items.Move(index, target);
        Selected = item;
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    public void MoveItem(RebaseItemViewModel item, int targetIndex)
    {
        var index = Items.IndexOf(item);
        if (index < 0) return;
        Items.Move(index, Math.Clamp(targetIndex, 0, Items.Count - 1));
        Selected = item;
    }

    [RelayCommand]
    private void SetAction(RebaseAction action)
    {
        if (Selected is not null) Selected.Action = action;
    }

    [RelayCommand]
    private void Confirm()
    {
        var firstKept = Items.FirstOrDefault(i => !i.IsDropped);
        if (firstKept is { IsCombined: true })
        {
            Error = Loc.T("The first commit can't be squashed into a previous one.", "İlk commit kendinden öncekiyle squash edilemez.");
            return;
        }
        if (Items.All(i => i.IsDropped))
        {
            Error = Loc.T("At least one commit must be kept. To remove all commits, reset the branch instead.", "En az bir commit korunmalı. Tüm commit'leri kaldırmak için bunun yerine branch'i reset edin.");
            return;
        }
        Close(new InteractiveRebaseRequest(Items.Select(i => new RebaseTodoItem(i.Commit.Sha, i.Action,
            i.CanEditMessage && i.Message != i.Commit.Subject ? i.Message : null)).ToList()));
    }
}

public sealed record CommitStyleChoice(CommitMessageStyle Style);
