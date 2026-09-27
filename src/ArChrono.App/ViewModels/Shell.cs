using System.Collections.ObjectModel;
using ArChrono.App.Infrastructure;
using ArChrono.Application;
using ArChrono.Application.Settings;
using ArChrono.Git.Errors;
using ArChrono.Localization;
using ArChrono.Recovery.Operations;
using ArChrono.Storage.Records;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArChrono.App.ViewModels;

/// <summary>Alt ViewModel'lerin pencereye ait hizmetlere (diyalog, toast, dosya seçici) eriştiği arayüz.</summary>
public interface IShell
{
    AppServices Services { get; }

    bool IsPro { get; }

    Task<TResult?> ShowDialogAsync<TResult>(DialogViewModel<TResult> dialog) where TResult : class;

    void ShowToast(ToastViewModel toast);

    Task ShowErrorAsync(GitError error, Func<Task>? undo = null);

    Task<string?> PickFolderAsync(string title);

    Task CopyToClipboardAsync(string text);
}

public interface IOverlay
{
    void Dismiss();
}

/// <summary>Diyalog kabuğunun (başlık, genişlik, kapat) bağlandığı jenerik olmayan taban.</summary>
public abstract class DialogViewModelBase : ViewModelBase, IOverlay
{
    private RelayCommand? _dismiss;

    public abstract string Title { get; }

    public virtual double DialogWidth => 540;

    public IRelayCommand DismissCommand => _dismiss ??= new RelayCommand(Dismiss);

    public abstract void Dismiss();
}

/// <summary>Pencere içi modal diyalog. Sonuç <see cref="Completion"/> ile beklenir; iptal = null.</summary>
public abstract partial class DialogViewModel<TResult> : DialogViewModelBase where TResult : class
{
    private readonly TaskCompletionSource<TResult?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<TResult?> Completion => _completion.Task;

    protected void Close(TResult? result) => _completion.TrySetResult(result);

    [RelayCommand]
    public void Cancel() => Close(null);

    public override void Dismiss() => Cancel();
}

public enum ToastKind
{
    Success,
    Info,
    Warning,
    Time,
    Error,
}

public sealed record ToastAction(string Label, Func<Task> Execute);

public sealed partial class ToastViewModel : ObservableObject
{
    public ToastViewModel(string message, ToastKind kind = ToastKind.Success, string? detail = null, params ToastAction[] actions)
    {
        Message = message;
        Kind = kind;
        Detail = detail;
        Actions = actions;
    }

    public string Message { get; }
    public string? Detail { get; }
    public ToastKind Kind { get; }
    public IReadOnlyList<ToastAction> Actions { get; }
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public string IconKey => Kind switch
    {
        ToastKind.Success => "Icon.Check",
        ToastKind.Warning or ToastKind.Error => "Icon.Alert",
        ToastKind.Time => "Icon.TimeMachine",
        _ => "Icon.Info",
    };

    public string AccentKey => Kind switch
    {
        ToastKind.Success => "Chrono.Safe",
        ToastKind.Warning or ToastKind.Time => "Chrono.Time",
        ToastKind.Error => "Chrono.Danger",
        _ => "Chrono.Info",
    };

    public Action<ToastViewModel>? DismissRequested { get; set; }

    [RelayCommand]
    private async Task RunAction(ToastAction action)
    {
        DismissRequested?.Invoke(this);
        await action.Execute();
    }

    [RelayCommand]
    private void Dismiss() => DismissRequested?.Invoke(this);
}

/// <summary>Uygulama kabuğu: içerik (Home / Repository), overlay (diyalog, palet), toast'lar.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IShell
{
    private readonly Stack<ViewModelBase> _overlays = new();

    public MainWindowViewModel(AppServices services)
    {
        Services = services;
        Home = new HomeViewModel(this);
        Content = Home;
        IsPro = services.Settings.Current.Mode == UiMode.Pro;
        services.Settings.Changed += (_, s) => OnUi(() => IsPro = s.Mode == UiMode.Pro);
    }

    public AppServices Services { get; }

    [ObservableProperty]
    public partial HomeViewModel Home { get; private set; }

    [ObservableProperty]
    public partial ViewModelBase Content { get; set; }

    [ObservableProperty]
    public partial ViewModelBase? Overlay { get; set; }

    [ObservableProperty]
    public partial bool IsPro { get; set; }

    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    public RepositoryViewModel? Repository => Content as RepositoryViewModel;

    public string WindowTitle => Repository is { } repo ? $"{repo.Name} — ArChrono" : "ArChrono";

    /// <summary>Pencere tarafından atanır (StorageProvider/Clipboard TopLevel'a bağlıdır).</summary>
    public Func<string, Task<string?>>? FolderPicker { get; set; }

    public Func<string, Task>? ClipboardWriter { get; set; }

    public Action? AboutPresenter { get; set; }

    // --- Dil ---
    public LanguagePreference LanguagePreference => Services.Settings.Current.Language;
    public bool IsSystemLanguagePreferred => LanguagePreference == LanguagePreference.System;
    public bool IsEnglishPreferred => LanguagePreference == LanguagePreference.English;
    public bool IsTurkishPreferred => LanguagePreference == LanguagePreference.Turkish;
    public string LanguageCode => Loc.IsTurkish ? "TR" : "EN";

    partial void OnContentChanged(ViewModelBase value)
    {
        OnPropertyChanged(nameof(Repository));
        OnPropertyChanged(nameof(WindowTitle));
    }

    partial void OnIsProChanged(bool value)
    {
        var mode = value ? UiMode.Pro : UiMode.Guided;
        if (Services.Settings.Current.Mode != mode) Services.Settings.Update(s => s with { Mode = mode, OnboardingCompleted = true });
        Repository?.OnModeChanged();
    }

    public async Task<TResult?> ShowDialogAsync<TResult>(DialogViewModel<TResult> dialog) where TResult : class
    {
        if (Overlay is not null) _overlays.Push(Overlay);
        Overlay = dialog;
        try
        {
            return await dialog.Completion;
        }
        finally
        {
            Overlay = _overlays.Count > 0 ? _overlays.Pop() : null;
        }
    }

    public void ShowOverlay(ViewModelBase overlay) => Overlay = overlay;

    public void CloseOverlay()
    {
        if (Overlay is IOverlay dismissible) dismissible.Dismiss();
        else Overlay = _overlays.Count > 0 ? _overlays.Pop() : null;
    }

    public void ShowToast(ToastViewModel toast)
    {
        OnUi(() =>
        {
            toast.DismissRequested = t => Toasts.Remove(t);
            Toasts.Add(toast);
            while (Toasts.Count > 4) Toasts.RemoveAt(0);
            var lifetime = toast.Kind is ToastKind.Error or ToastKind.Warning || toast.Actions.Count > 0 ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(5);
            Avalonia.Threading.DispatcherTimer.RunOnce(() => Toasts.Remove(toast), lifetime);
        });
    }

    public async Task ShowErrorAsync(GitError error, Func<Task>? undo = null)
    {
        var result = await ShowDialogAsync(new Dialogs.ErrorDialogViewModel(error, undo is not null));
        if (result?.Undo == true && undo is not null) await undo();
    }

    public async Task<string?> PickFolderAsync(string title) => FolderPicker is null ? null : await FolderPicker(title);

    public async Task CopyToClipboardAsync(string text)
    {
        if (ClipboardWriter is not null) await ClipboardWriter(text);
        ShowToast(new ToastViewModel(Loc.T("Copied to clipboard", "Panoya kopyalandı"), ToastKind.Info));
    }

    public async Task OpenRepositoryAsync(string path)
    {
        try
        {
            var session = await Services.OpenSessionAsync(path);
            if (Repository is { } current && current.Session != session) await CloseRepositoryCoreAsync(current);
            var repository = new RepositoryViewModel(this, session);
            Content = repository;
            await repository.InitializeAsync();
        }
        catch (GitException ex)
        {
            await ShowErrorAsync(ex.Error);
        }
    }

    [RelayCommand]
    public async Task CloseRepository()
    {
        if (Repository is { } current) await CloseRepositoryCoreAsync(current);
        Home.Reload();
        Content = Home;
    }

    private async Task CloseRepositoryCoreAsync(RepositoryViewModel repository)
    {
        repository.Detach();
        await Services.CloseSessionAsync(repository.Session);
    }

    [RelayCommand]
    public void OpenCommandPalette() => ShowOverlay(new CommandPaletteViewModel(this, string.Empty));

    [RelayCommand]
    public async Task OpenSettings() => await ShowDialogAsync(new Dialogs.SettingsViewModel(this));

    [RelayCommand]
    private void SetGuided() => IsPro = false;

    [RelayCommand]
    private void SetPro() => IsPro = true;

    [RelayCommand]
    private void ShowAbout() => AboutPresenter?.Invoke();

    /// <summary>Gece ↔ gündüz. "Sistemle aynı" tercihindeyken o an görünen temanın tersine geçer.</summary>
    [RelayCommand]
    public void ToggleDayNight()
    {
        var isNight = Avalonia.Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        var next = isNight ? ThemePreference.Light : ThemePreference.Dark;
        Services.Settings.Update(s => s with { Theme = next });
        App.ApplyTheme(next);
    }

    [RelayCommand]
    public void SetLanguage(LanguagePreference preference)
    {
        if (Services.Settings.Current.Language != preference) Services.Settings.Update(s => s with { Language = preference });
        var changed = App.ApplyLanguage(preference);
        foreach (var name in new[] { nameof(LanguagePreference), nameof(IsSystemLanguagePreferred), nameof(IsEnglishPreferred), nameof(IsTurkishPreferred), nameof(LanguageCode) })
            OnPropertyChanged(name);
        if (!changed) return;
        // Menü/flyout kapanmayı bitirsin; ardından görünümler yeni dilde yeniden kurulur.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = RebuildForLanguageAsync());
    }

    /// <summary>
    /// Metinler görünüm ve ViewModel oluşturulurken seçilir; dil değişince içerik yeniden kurulur.
    /// Açık depo, bölüm, Git Console ve yazılmakta olan commit mesajı korunur.
    /// </summary>
    private async Task RebuildForLanguageAsync()
    {
        Toasts.Clear();
        Views.AboutWindow.RefreshTitle();
        if (Overlay is { } overlay)
        {
            // Açık diyalog (dil Ayarlar'dan değiştirildiyse) yerinde kalır; görünümü yeni dilde yeniden oluşturulur.
            (overlay as Dialogs.SettingsViewModel)?.RefreshTexts();
            Overlay = null;
            Overlay = overlay;
        }

        Home = new HomeViewModel(this);
        if (Repository is { } current)
        {
            current.Detach();
            var rebuilt = new RepositoryViewModel(this, current.Session)
            {
                Section = current.Section,
                IsConsoleOpen = current.IsConsoleOpen,
            };
            rebuilt.Workspace.Changes.Message = current.Workspace.Changes.Message;
            Content = rebuilt;
            await rebuilt.InitializeAsync();
        }
        else
        {
            Content = Home;
        }
    }

    /// <summary>
    /// İşlem sonucunu kullanıcıya standart biçimde gösterir: başarı → toast + Undo;
    /// conflict → çözüm yönlendirmesi; hata → anlaşılır hata diyaloğu (+ teknik detay).
    /// </summary>
    public async Task PresentOutcomeAsync(OperationOutcome outcome, Func<Task>? undo, Func<Task>? viewRecoveryPoint, Func<Task>? resolve = null, string? successMessage = null)
    {
        var actions = new List<ToastAction>();
        if (outcome.CanUndo && undo is not null) actions.Add(new ToastAction(Loc.T("Undo", "Geri al"), undo));

        switch (outcome.Status)
        {
            case OperationStatus.Blocked:
                ShowToast(new ToastViewModel(outcome.Validation.Problems.FirstOrDefault() ?? Loc.T("The operation could not start.", "İşlem başlatılamadı."), ToastKind.Warning));
                break;
            case OperationStatus.Succeeded when outcome.VerificationProblem is not null:
                ShowToast(new ToastViewModel(Loc.T("Finished, but the result looks unexpected.", "Tamamlandı, ancak sonuç beklenenden farklı görünüyor."), ToastKind.Warning, outcome.VerificationProblem, [.. actions]));
                break;
            case OperationStatus.Succeeded:
                if (viewRecoveryPoint is not null && outcome.Before is not null) actions.Add(new ToastAction(Loc.T("View recovery point", "Kurtarma noktasını göster"), viewRecoveryPoint));
                ShowToast(new ToastViewModel(successMessage ?? outcome.Operation.Title, ToastKind.Success,
                    outcome.Before is not null ? Loc.T($"Recovery point saved at {outcome.Before.CreatedAt:HH:mm:ss}", $"Kurtarma noktası {outcome.Before.CreatedAt:HH:mm:ss} anında kaydedildi") : outcome.Execution?.Message, [.. actions]));
                break;
            case OperationStatus.Conflicted:
                if (resolve is not null) actions.Insert(0, new ToastAction(Loc.T("Resolve", "Çöz"), resolve));
                ShowToast(new ToastViewModel(outcome.Execution?.Message ?? Loc.T("Stopped: some files need your decision.", "Durdu: bazı dosyalar kararınızı bekliyor."), ToastKind.Time,
                    Loc.T("Nothing is lost. Resolve the conflicts, or undo to go back.", "Hiçbir şey kaybolmadı. Çakışmaları çözün ya da geri dönmek için geri alın."), [.. actions]));
                break;
            case OperationStatus.Cancelled:
                ShowToast(new ToastViewModel(Loc.T("Cancelled.", "İptal edildi."), ToastKind.Info));
                break;
            default:
                var error = outcome.Error ?? GitError.Simple(GitErrorCode.Unknown, Loc.T("The operation failed.", "İşlem başarısız oldu."), outcome.Operation.ErrorMessage ?? "");
                await ShowErrorAsync(error, outcome.CanUndo ? undo : null);
                break;
        }
    }
}
