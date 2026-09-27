using System.Runtime.InteropServices;
using ArChrono.Platform;
using Avalonia;

namespace ArChrono.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        Log.Initialize(AppPaths.ForCurrentUser());
        try
        {
            Log.Info($"ArChrono starting — {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}, .NET {Environment.Version}");
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Error("ArChrono terminated unexpectedly", ex);
            return 1;
        }
    }

    // Avalonia tasarımcısı da bu metodu kullanır.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new MacOSPlatformOptions { ShowInDock = true })
            .LogToTrace();
}
