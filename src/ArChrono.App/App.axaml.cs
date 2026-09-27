using ArChrono.App.ViewModels;
using ArChrono.App.Views;
using ArChrono.Application;
using ArChrono.Application.Settings;
using ArChrono.Localization;
using ArChrono.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;

namespace ArChrono.App;

public partial class App : Avalonia.Application
{
    private AppServices? _services;
    private NativeMenuItem? _aboutMenuItem;

    /// <summary>İşletim sisteminin dili; <see cref="UseInterfaceCulture"/> kültürü sabitlemeden önce okunur.</summary>
    private static readonly Lazy<AppLanguage> SystemLanguageValue = new(SystemLanguage.Detect);

    public static WindowIcon? WindowIcon { get; private set; }

    public override void Initialize()
    {
        _ = SystemLanguageValue.Value;
        UseInterfaceCulture();
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Biçimlendirme ve karşılaştırmalar kültürden bağımsız kalsın diye kültür en-US'e sabitlenir
    /// (Türkçe "I/ı" dönüşümleri Git çıktısı işlemeyi bozmasın). Arayüz dili ayrıca <see cref="Loc"/> ile seçilir.
    /// </summary>
    public static void UseInterfaceCulture()
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
        System.Globalization.CultureInfo.CurrentCulture = culture;
        System.Globalization.CultureInfo.CurrentUICulture = culture;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Log.Error("Unhandled UI exception", e.Exception);
                e.Handled = true;
            };

            _services = AppServices.Create();
            ApplyLanguage(_services.Settings.Current.Language);
            ApplyTheme(_services.Settings.Current.Theme);
            var shell = new MainWindowViewModel(_services);
            var window = new MainWindow { DataContext = shell };
            try
            {
                WindowIcon = new WindowIcon(Services.AppIcon.CreateBitmap(256));
                window.Icon = WindowIcon;
            }
            catch (Exception ex)
            {
                Log.Error("Could not render the window icon", ex);
            }
            desktop.MainWindow = window;
            desktop.ShutdownRequested += async (_, _) =>
            {
                if (_services is not null) await _services.DisposeAsync();
            };
            CreateMacApplicationMenu(window);

            var startPath = desktop.Args?.FirstOrDefault(a => !a.StartsWith('-') && Directory.Exists(a));
            if (startPath is not null) Dispatcher.UIThread.Post(() => _ = shell.OpenRepositoryAsync(startPath));
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>macOS uygulama menüsü: "ArChrono Hakkında" (ArSnap ile aynı).</summary>
    private void CreateMacApplicationMenu(Window owner)
    {
        if (!OperatingSystem.IsMacOS()) return;
        _aboutMenuItem = new NativeMenuItem(Loc.T("About ArChrono", "ArChrono Hakkında"));
        _aboutMenuItem.Click += (_, _) => AboutWindow.ShowSingle(owner);
        var menu = new NativeMenu();
        menu.Items.Add(_aboutMenuItem);
        NativeMenu.SetMenu(this, menu);
        Loc.LanguageChanged += (_, _) => _aboutMenuItem.Header = Loc.T("About ArChrono", "ArChrono Hakkında");
    }

    public static void ApplyTheme(ThemePreference theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme switch
        {
            ThemePreference.Light => ThemeVariant.Light,
            ThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    /// <summary>Tercihi dile çevirip uygular. Dil gerçekten değiştiyse true döner.</summary>
    public static bool ApplyLanguage(LanguagePreference preference)
    {
        var language = preference switch
        {
            LanguagePreference.English => AppLanguage.English,
            LanguagePreference.Turkish => AppLanguage.Turkish,
            _ => SystemLanguageValue.Value,
        };
        var changed = Loc.Language != language;
        Loc.SetLanguage(language);
        return changed;
    }
}
