using System.Collections.ObjectModel;
using ArChrono.AI.Context;
using ArChrono.AI.Privacy;
using ArChrono.AI.Providers;
using ArChrono.App.Infrastructure;
using ArChrono.Application.Settings;
using ArChrono.Git.Services;
using ArChrono.Localization;
using ArChrono.Platform.Credentials;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArChrono.App.ViewModels.Dialogs;

public sealed partial class AiExplanationDialogViewModel : DialogViewModel<Confirmation>
{
    public AiExplanationDialogViewModel(string title, AiExplanationViewModel explanation)
    {
        DialogTitle = title;
        Explanation = explanation;
        if (explanation.IsIdle) explanation.ExplainCommand.Execute(null);
    }

    public string DialogTitle { get; }
    public override string Title => DialogTitle;
    public override double DialogWidth => 640;
    public AiExplanationViewModel Explanation { get; }
}

public enum SettingsTab
{
    General,
    TimeMachine,
    Ai,
    Git,
}

/// <summary>Ayarlar: General · Time Machine &amp; Storage · AI &amp; Privacy · Git. Değişiklikler anında kaydedilir.</summary>
public sealed partial class SettingsViewModel : DialogViewModel<Confirmation>
{
    private readonly MainWindowViewModel _shell;
    private bool _loading = true;

    public SettingsViewModel(MainWindowViewModel shell)
    {
        _shell = shell;
        var s = shell.Services.Settings.Current;
        IsPro = s.Mode == UiMode.Pro;
        Theme = s.Theme;
        Language = s.Language;
        TerminalApplication = s.TerminalApplication ?? string.Empty;
        TimeMachineEnabled = s.TimeMachine.Enabled;
        IntervalMinutes = s.TimeMachine.IntervalMinutes;
        Keep = s.TimeMachine.Keep;
        MaxStorageGigabytes = s.TimeMachine.MaxStorageGigabytes;
        MaxFileSizeMegabytes = s.TimeMachine.MaxFileSizeMegabytes;
        PinInRepository = s.TimeMachine.PinInRepository;
        AiEnabled = s.Ai.Enabled;
        Provider = s.Ai.Provider.Kind;
        Endpoint = s.Ai.Provider.Endpoint ?? string.Empty;
        Model = s.Ai.Provider.Model;
        Deployment = s.Ai.Provider.Deployment ?? string.Empty;
        AlwaysPreview = s.Ai.AlwaysPreview;
        CommitStyle = s.Ai.CommitStyle;
        ExcludedPatterns = string.Join("\n", s.Ai.ExcludedPatterns);
        PullMode = s.Git.PullMode;
        AutoFetch = s.Git.AutoFetch;
        Autostash = s.Git.Autostash;
        GitPath = s.Git.ExecutablePath ?? string.Empty;
        _loading = false;
        RefreshKeyStatus();
        _ = LoadIdentityAsync();
        StorageUsage = DescribeStorageUsage();
    }

    /// <summary>Dil değişince ViewModel'de saklanan metinleri yeni dilde yeniden üretir.</summary>
    public void RefreshTexts()
    {
        StorageUsage = DescribeStorageUsage();
        CleanupResult = string.Empty;
        TestResult = string.Empty;
        RefreshKeyStatus();
    }

    private string DescribeStorageUsage()
    {
        var blobs = _shell.Services.Storage.Blobs;
        return Loc.T($"{Format.Bytes(blobs.GetTotalStoredBytes())} used · {blobs.Count()} stored file versions",
            $"{Format.Bytes(blobs.GetTotalStoredBytes())} kullanıldı · {blobs.Count()} dosya sürümü saklanıyor");
    }

    public override string Title => Loc.T("Settings", "Ayarlar");
    public override double DialogWidth => 800;

    public static IReadOnlyList<ThemePreference> Themes { get; } = Enum.GetValues<ThemePreference>();
    public static IReadOnlyList<LanguagePreference> Languages { get; } = Enum.GetValues<LanguagePreference>();
    public static IReadOnlyList<SnapshotRetention> Retentions { get; } = Enum.GetValues<SnapshotRetention>();
    public static IReadOnlyList<AiProviderKind> Providers { get; } = Enum.GetValues<AiProviderKind>();
    public static IReadOnlyList<CommitMessageStyle> Styles { get; } = Enum.GetValues<CommitMessageStyle>();
    public static IReadOnlyList<PullMode> PullModes { get; } = Enum.GetValues<PullMode>();
    public static IReadOnlyList<int> Intervals { get; } = [1, 5, 15, 30];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneral), nameof(IsTimeMachine), nameof(IsAi), nameof(IsGit))]
    public partial SettingsTab Tab { get; set; }

    public bool IsGeneral { get => Tab == SettingsTab.General; set { if (value) Tab = SettingsTab.General; } }
    public bool IsTimeMachine { get => Tab == SettingsTab.TimeMachine; set { if (value) Tab = SettingsTab.TimeMachine; } }
    public bool IsAi { get => Tab == SettingsTab.Ai; set { if (value) Tab = SettingsTab.Ai; } }
    public bool IsGit { get => Tab == SettingsTab.Git; set { if (value) Tab = SettingsTab.Git; } }

    // General
    [ObservableProperty] public partial bool IsPro { get; set; }
    [ObservableProperty] public partial ThemePreference Theme { get; set; }
    [ObservableProperty] public partial LanguagePreference Language { get; set; }
    [ObservableProperty] public partial string TerminalApplication { get; set; }

    // Time Machine
    [ObservableProperty] public partial bool TimeMachineEnabled { get; set; }
    [ObservableProperty] public partial int IntervalMinutes { get; set; }
    [ObservableProperty] public partial SnapshotRetention Keep { get; set; }
    [ObservableProperty] public partial double MaxStorageGigabytes { get; set; }
    [ObservableProperty] public partial int MaxFileSizeMegabytes { get; set; }
    [ObservableProperty] public partial bool PinInRepository { get; set; }
    [ObservableProperty] public partial string StorageUsage { get; set; }
    [ObservableProperty] public partial string CleanupResult { get; set; } = string.Empty;

    // AI
    [ObservableProperty] public partial bool AiEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SuggestedModels), nameof(IsAzure), nameof(NeedsKey), nameof(EndpointWatermark), nameof(PrivacyNote))]
    public partial AiProviderKind Provider { get; set; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PrivacyNote))] public partial string Endpoint { get; set; }
    [ObservableProperty] public partial string Model { get; set; }
    [ObservableProperty] public partial string Deployment { get; set; }
    [ObservableProperty] public partial bool AlwaysPreview { get; set; }
    [ObservableProperty] public partial CommitMessageStyle CommitStyle { get; set; }
    [ObservableProperty] public partial string ExcludedPatterns { get; set; }
    [ObservableProperty] public partial string ApiKeyInput { get; set; } = string.Empty;
    [ObservableProperty] public partial string KeyStatus { get; set; } = string.Empty;
    [ObservableProperty] public partial string TestResult { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsTesting { get; set; }

    public IReadOnlyList<string> SuggestedModels => AiProviderSettings.SuggestedModels(Provider);
    public bool IsAzure => Provider == AiProviderKind.AzureOpenAI;
    public bool NeedsKey => Provider != AiProviderKind.Ollama;
    public string EndpointWatermark => AiProviderSettings.DefaultEndpoint(Provider) is { Length: > 0 } d ? d : "https://…";
    public string CredentialStoreName => _shell.Services.Credentials.Description;

    public string PrivacyNote
    {
        get
        {
            var settings = BuildProvider();
            return settings.IsLocal
                ? Loc.T("Requests stay on this computer.", "İstekler bu bilgisayarda kalır.")
                : Uri.TryCreate(settings.EffectiveEndpoint, UriKind.Absolute, out var uri)
                    ? Loc.T($"Selected diffs are sent to {uri.Host} only when you ask for an AI feature. Sensitive files are never included.",
                        $"Seçilen diff'ler yalnızca bir AI özelliği istediğinizde {uri.Host} adresine gönderilir. Hassas dosyalar asla dahil edilmez.")
                    : Loc.T("Selected diffs are sent to the provider only when you ask for an AI feature. Sensitive files are never included.",
                        "Seçilen diff'ler yalnızca bir AI özelliği istediğinizde sağlayıcıya gönderilir. Hassas dosyalar asla dahil edilmez.");
        }
    }

    // Git
    [ObservableProperty] public partial PullMode PullMode { get; set; }
    [ObservableProperty] public partial bool AutoFetch { get; set; }
    [ObservableProperty] public partial bool Autostash { get; set; }
    [ObservableProperty] public partial string GitPath { get; set; }
    [ObservableProperty] public partial string UserName { get; set; } = string.Empty;
    [ObservableProperty] public partial string UserEmail { get; set; } = string.Empty;
    public string GitVersion => _shell.Services.Git?.VersionText ?? Loc.T("Git not found", "Git bulunamadı");

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading) return;
        switch (e.PropertyName)
        {
            case nameof(Tab) or nameof(ApiKeyInput) or nameof(KeyStatus) or nameof(TestResult) or nameof(IsTesting) or nameof(CleanupResult)
                or nameof(StorageUsage) or nameof(UserName) or nameof(UserEmail) or nameof(IsGeneral) or nameof(IsTimeMachine) or nameof(IsAi) or nameof(IsGit)
                or nameof(SuggestedModels) or nameof(IsAzure) or nameof(NeedsKey) or nameof(EndpointWatermark) or nameof(PrivacyNote):
                return;
            case nameof(Provider):
                _loading = true;
                Model = AiProviderSettings.DefaultModel(Provider);
                Endpoint = string.Empty;
                _loading = false;
                RefreshKeyStatus();
                break;
            case nameof(Theme):
                App.ApplyTheme(Theme);
                break;
            case nameof(Language):
                _shell.SetLanguage(Language);
                return;
            case nameof(IsPro):
                _shell.IsPro = IsPro;
                break;
        }
        Save();
    }

    private AiProviderSettings BuildProvider() => new()
    {
        Kind = Provider,
        Endpoint = string.IsNullOrWhiteSpace(Endpoint) ? null : Endpoint.Trim(),
        Model = string.IsNullOrWhiteSpace(Model) ? AiProviderSettings.DefaultModel(Provider) : Model.Trim(),
        Deployment = string.IsNullOrWhiteSpace(Deployment) ? null : Deployment.Trim(),
    };

    private void Save()
    {
        _shell.Services.Settings.Update(s => s with
        {
            Theme = Theme,
            TerminalApplication = string.IsNullOrWhiteSpace(TerminalApplication) ? null : TerminalApplication.Trim(),
            TimeMachine = s.TimeMachine with
            {
                Enabled = TimeMachineEnabled,
                IntervalMinutes = IntervalMinutes,
                Keep = Keep,
                MaxStorageGigabytes = Math.Clamp(MaxStorageGigabytes, 0.1, 1000),
                MaxFileSizeMegabytes = Math.Clamp(MaxFileSizeMegabytes, 1, 2048),
                PinInRepository = PinInRepository,
            },
            Ai = s.Ai with
            {
                Enabled = AiEnabled,
                Provider = BuildProvider(),
                AlwaysPreview = AlwaysPreview,
                CommitStyle = CommitStyle,
                ExcludedPatterns = ExcludedPatterns.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            },
            Git = s.Git with
            {
                PullMode = PullMode,
                AutoFetch = AutoFetch,
                Autostash = Autostash,
                ExecutablePath = string.IsNullOrWhiteSpace(GitPath) ? null : GitPath.Trim(),
            },
        });
        _shell.Repository?.RefreshChrono();
    }

    private void RefreshKeyStatus()
    {
        if (!_shell.Services.Credentials.IsAvailable)
        {
            KeyStatus = _shell.Services.Credentials.Description;
            return;
        }
        KeyStatus = Provider == AiProviderKind.Ollama ? Loc.T("No key needed for local Ollama.", "Yerel Ollama için anahtar gerekmez.")
            : _shell.Services.Ai.HasApiKey(Provider) ? Loc.T($"A key is saved in {CredentialStoreName}.", $"{CredentialStoreName} içinde kayıtlı bir anahtar var.") : Loc.T("No key saved.", "Kayıtlı anahtar yok.");
    }

    [RelayCommand]
    private void SaveKey()
    {
        if (string.IsNullOrWhiteSpace(ApiKeyInput)) return;
        try
        {
            _shell.Services.Ai.SaveApiKey(Provider, ApiKeyInput);
            ApiKeyInput = string.Empty;
        }
        catch (CredentialStoreException ex)
        {
            KeyStatus = ex.Message;
            return;
        }
        RefreshKeyStatus();
    }

    [RelayCommand]
    private void RemoveKey()
    {
        _shell.Services.Ai.RemoveApiKey(Provider);
        RefreshKeyStatus();
    }

    [RelayCommand]
    private void ResetExclusions() => ExcludedPatterns = string.Join("\n", SensitivePathFilter.DefaultPatterns);

    [RelayCommand]
    private async Task TestConnection()
    {
        IsTesting = true;
        TestResult = Loc.T("Testing…", "Test ediliyor…");
        try
        {
            TestResult = await Task.Run(() => _shell.Services.Ai.TestConnectionAsync());
        }
        catch (Exception ex) when (ex is AiProviderException or CredentialStoreException)
        {
            TestResult = ex.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task RunCleanup()
    {
        CleanupResult = Loc.T("Cleaning up…", "Temizleniyor…");
        var services = _shell.Services;
        var report = await Task.Run(() => services.Recovery.Retention.RunAsync(services.Settings.Current.TimeMachine.ToPolicy()));
        CleanupResult = Loc.T($"Removed {report.DeletedSnapshots} snapshot(s) and {report.DeletedRecoveryPoints} recovery point(s), freed {Format.Bytes(report.FreedBytes)}.", $"{report.DeletedSnapshots} anlık görüntü ve {report.DeletedRecoveryPoints} kurtarma noktası silindi, {Format.Bytes(report.FreedBytes)} yer açıldı.");
        StorageUsage = DescribeStorageUsage();
    }

    private async Task LoadIdentityAsync()
    {
        if (_shell.Services.Runner is null) return;
        var client = _shell.Services.CreateClient();
        UserName = await Task.Run(() => client.GetGlobalConfigAsync("user.name")) ?? string.Empty;
        UserEmail = await Task.Run(() => client.GetGlobalConfigAsync("user.email")) ?? string.Empty;
    }

    [RelayCommand]
    private async Task SaveIdentity()
    {
        if (_shell.Services.Runner is null) return;
        var client = _shell.Services.CreateClient();
        try
        {
            if (!string.IsNullOrWhiteSpace(UserName)) await Task.Run(() => client.SetGlobalConfigAsync("user.name", UserName.Trim()));
            if (!string.IsNullOrWhiteSpace(UserEmail)) await Task.Run(() => client.SetGlobalConfigAsync("user.email", UserEmail.Trim()));
            _shell.ShowToast(new ToastViewModel(Loc.T("Git identity saved", "Git kimliği kaydedildi"), ToastKind.Success));
        }
        catch (Git.Errors.GitException ex)
        {
            await _shell.ShowErrorAsync(ex.Error);
        }
    }

    [RelayCommand]
    private void SetTab(SettingsTab tab) => Tab = tab;

    [RelayCommand]
    private void Done() => Close(new Confirmation(true));
}
