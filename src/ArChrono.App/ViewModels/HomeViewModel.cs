using System.Collections.ObjectModel;
using ArChrono.App.Infrastructure;
using ArChrono.App.ViewModels.Dialogs;
using ArChrono.Application.Settings;
using ArChrono.Git.Errors;
using ArChrono.Localization;
using ArChrono.Storage.Records;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArChrono.App.ViewModels;

public sealed partial class RecentRepositoryItem(RepositoryRecord record, HomeViewModel owner) : ObservableObject
{
    public RepositoryRecord Record { get; } = record;
    public HomeViewModel Owner { get; } = owner;
    public string Name => Record.Name;
    public string Path => Record.RootPath.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "~");
    public string LastOpened => Record.LastOpenedAt is { } time ? Format.Relative(time) : "";
    public bool IsFavorite => Record.IsFavorite;
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();
}

/// <summary>Repository manager: son kullanılanlar, aç/klonla/oluştur ve ilk kullanımda mod seçimi.</summary>
public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _shell;

    public HomeViewModel(MainWindowViewModel shell)
    {
        _shell = shell;
        Reload();
    }

    public MainWindowViewModel Shell => _shell;

    public ObservableCollection<RecentRepositoryItem> Recent { get; } = [];

    public bool HasRecent => Recent.Count > 0;

    public bool ShowOnboarding => !_shell.Services.Settings.Current.OnboardingCompleted;

    public bool GitAvailable => _shell.Services.Git is not null;

    public string GitStatus => _shell.Services.Git is { } git
        ? Loc.T($"Using {git.VersionText} · {git.Path}", $"Kullanılan: {git.VersionText} · {git.Path}") + (git.IsSupported ? "" : Loc.T(" — please update to Git 2.38 or newer", " — lütfen Git 2.38 veya daha yeni bir sürüme güncelleyin"))
        : Loc.T("Git was not found on this computer.", "Bu bilgisayarda Git bulunamadı.");

    public string GitInstallHint => OperatingSystem.IsWindows()
        ? Loc.T("Install Git for Windows (git-scm.com), then restart ArChrono.", "Git for Windows'u (git-scm.com) kurun, sonra ArChrono'yu yeniden başlatın.")
        : OperatingSystem.IsMacOS() ? Loc.T("Run 'xcode-select --install' in Terminal or install Git with Homebrew, then restart ArChrono.", "Terminal'de 'xcode-select --install' çalıştırın veya Git'i Homebrew ile kurun, sonra ArChrono'yu yeniden başlatın.")
        : Loc.T("Install git with your package manager, then restart ArChrono.", "Git'i paket yöneticinizle kurun, sonra ArChrono'yu yeniden başlatın.");

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? BusyText { get; set; }

    public void Reload()
    {
        Recent.Clear();
        foreach (var record in _shell.Services.GetRecentRepositories()) Recent.Add(new RecentRepositoryItem(record, this));
        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(ShowOnboarding));
    }

    [RelayCommand]
    private async Task Open()
    {
        if (await _shell.PickFolderAsync(Loc.T("Open a Git repository", "Bir Git deposu aç")) is { } folder) await OpenPathAsync(folder);
    }

    [RelayCommand]
    private async Task OpenRecent(RecentRepositoryItem item) => await OpenPathAsync(item.Record.RootPath);

    public async Task OpenPathAsync(string path)
    {
        IsBusy = true;
        BusyText = Loc.T("Opening repository…", "Depo açılıyor…");
        try
        {
            await _shell.OpenRepositoryAsync(path);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Clone()
    {
        var request = await _shell.ShowDialogAsync(new CloneDialogViewModel(_shell));
        if (request is null) return;
        IsBusy = true;
        BusyText = Loc.T("Cloning ", "Klonlanıyor: ") + request.Url;
        try
        {
            var progress = new Progress<string>(line => BusyText = line);
            var session = await _shell.Services.CloneAsync(request.Url, request.Destination, progress);
            await _shell.OpenRepositoryAsync(session.RootPath);
        }
        catch (GitException ex)
        {
            await _shell.ShowErrorAsync(ex.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Init()
    {
        var folder = await _shell.PickFolderAsync(Loc.T("Choose a folder for the new repository", "Yeni depo için bir klasör seçin"));
        if (folder is null) return;
        try
        {
            var session = await _shell.Services.InitAsync(folder);
            await _shell.OpenRepositoryAsync(session.RootPath);
        }
        catch (GitException ex)
        {
            await _shell.ShowErrorAsync(ex.Error);
        }
    }

    [RelayCommand]
    private void ToggleFavorite(RecentRepositoryItem item)
    {
        _shell.Services.Storage.Repositories.SetFavorite(item.Record.Id, !item.IsFavorite);
        Reload();
    }

    [RelayCommand]
    private void Remove(RecentRepositoryItem item)
    {
        _shell.Services.RemoveFromRecent(item.Record.Id);
        Reload();
    }

    [RelayCommand]
    private void ChooseGuided() => Complete(UiMode.Guided);

    [RelayCommand]
    private void ChoosePro() => Complete(UiMode.Pro);

    private void Complete(UiMode mode)
    {
        _shell.Services.Settings.Update(s => s with { Mode = mode, OnboardingCompleted = true });
        _shell.IsPro = mode == UiMode.Pro;
        OnPropertyChanged(nameof(ShowOnboarding));
    }
}
