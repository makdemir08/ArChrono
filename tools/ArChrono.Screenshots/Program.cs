using System.Diagnostics;
using ArChrono.App;
using ArChrono.App.ViewModels;
using ArChrono.App.ViewModels.Dialogs;
using ArChrono.Application;
using ArChrono.Application.Settings;
using ArChrono.Git.Services;
using ArChrono.Localization;
using ArChrono.Platform;
using ArChrono.Storage.Records;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace ArChrono.Screenshots;

/// <summary>
/// Gerçek bir demo repository üzerinde uygulamayı headless (Skia) render eder ve ekran görüntüleri üretir.
/// Kullanım: dotnet run --project tools/ArChrono.Screenshots -- [çıktı dizini] [--lang en|tr]
/// </summary>
internal static class Program
{
    private static string _output = "";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--icon", var iconPath, .. var rest])
        {
            AppBuilder.Configure<App.App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            var size = rest is [var sizeText, ..] && int.TryParse(sizeText, out var parsed) ? parsed : 1024;
            using var icon = ArChrono.App.Services.AppIcon.CreateBitmap(size);
            icon.Save(Path.GetFullPath(iconPath));
            Console.WriteLine("Icon written to " + Path.GetFullPath(iconPath));
            return 0;
        }

        var langIndex = Array.IndexOf(args, "--lang");
        if (langIndex >= 0 && langIndex + 1 < args.Length)
        {
            Loc.SetLanguage(args[langIndex + 1] == "tr" ? AppLanguage.Turkish : AppLanguage.English);
            args = args.Where((_, i) => i != langIndex && i != langIndex + 1).ToArray();
        }

        _output = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine("docs", "screenshots"));
        Directory.CreateDirectory(_output);

        var workRoot = Path.Combine(Path.GetTempPath(), "archrono-screens-" + Guid.NewGuid().ToString("N")[..8]);
        var repoPath = Path.Combine(workRoot, "acme-portal");
        Directory.CreateDirectory(repoPath);
        DemoRepository.Create(repoPath);

        AppBuilder.Configure<App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();
        SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());

        var paths = new AppPaths(Path.Combine(workRoot, "data"), Path.Combine(workRoot, "logs"));
        Directory.CreateDirectory(paths.DataDirectory);
        Directory.CreateDirectory(paths.LogDirectory);
        var services = AppServices.Create(paths);
        services.Settings.Update(s => s with
        {
            OnboardingCompleted = false,
            Theme = ThemePreference.Dark,
            Git = s.Git with { AutoFetch = false },
            TimeMachine = s.TimeMachine with { IntervalMinutes = 120 },
        });
        services.Storage.Repositories.Upsert(Path.Combine(workRoot, "billing-service"), "x", "x", "billing-service", "sha1");
        services.Storage.Repositories.Upsert(Path.Combine(workRoot, "design-system"), "x", "x", "design-system", "sha1");

        App.App.ApplyTheme(ThemePreference.Dark);
        var shell = new MainWindowViewModel(services);
        var window = new ArChrono.App.Views.MainWindow { DataContext = shell, Width = 1480, Height = 920 };
        window.Show();
        Pump(300);
        Capture(window, "01-home");

        shell.Home.ChooseProCommand.Execute(null);
        Wait(shell.OpenRepositoryAsync(repoPath));
        var repo = shell.Repository!;
        Pump(800);

        SeedHistory(repo, repoPath);
        Wait(repo.RefreshAsync(ArChrono.Application.Repositories.RepositoryChange.All));
        Pump(600);

        // Çalışma alanı: commit composer
        repo.Workspace.SelectWorkingTreeCommand.Execute(null);
        Pump(900);
        Capture(window, "02-workspace-changes");

        // Commit detayı
        repo.Workspace.SelectedRow = repo.Workspace.Rows.First(r => r.IsCommit && r.Commit!.Parents.Count == 1 && r.Commit.Subject.Contains("refresh token"));
        Pump(900);
        Capture(window, "03-workspace-commit");

        // Tehlikeli işlem onayı
        var confirm = new ConfirmOperationDialogViewModel("RESET --HARD", Loc.T("Throw away changes and go back", "Değişiklikleri at ve geri dön"),
            [
                Loc.T("main will move to 4c1e9a2 \"Add login form validation\".", "main, 4c1e9a2 \"Add login form validation\" commit'ine taşınacak."),
                Loc.T("3 uncommitted change(s) will be overwritten.", "Commit edilmemiş 3 değişikliğin üzerine yazılacak."),
            ],
            "git reset --hard 4c1e9a2", isPro: true, confirmText: "Reset");
        _ = shell.ShowDialogAsync(confirm);
        Pump(400);
        Capture(window, "04-confirm-reset-hard");
        confirm.CancelCommand.Execute(null);
        Pump(200);

        // Recovery Center
        repo.Section = RepositorySection.Recovery;
        Wait(repo.Recovery.ReloadAsync());
        Pump(300);
        var point = repo.Recovery.Items.FirstOrDefault(i => i.Entry?.Title.Contains("reset", StringComparison.OrdinalIgnoreCase) == true)
                    ?? repo.Recovery.Items.FirstOrDefault(i => i.IsEntry);
        repo.Recovery.Selected = point;
        Pump(600);
        Capture(window, "05-recovery-center");

        // Time Machine
        repo.Section = RepositorySection.TimeMachine;
        Wait(repo.TimeMachine.LoadDayAsync());
        Pump(400);
        repo.TimeMachine.Selected = repo.TimeMachine.Snapshots.Skip(1).FirstOrDefault() ?? repo.TimeMachine.Snapshots.FirstOrDefault();
        Pump(1200);
        Capture(window, "06-time-machine");

        // Komut paleti (Guided)
        repo.Section = RepositorySection.Workspace;
        shell.IsPro = false;
        Pump(300);
        shell.OpenCommandPalette();
        Pump(600);
        Capture(window, "07-command-palette-guided");
        shell.CloseOverlay();
        Pump(200);

        // Açık tema
        repo.Workspace.SelectedRow = null;
        App.App.ApplyTheme(ThemePreference.Light);
        Pump(700);
        Capture(window, "08-workspace-light-guided");
        App.App.ApplyTheme(ThemePreference.Dark);
        shell.IsPro = true;

        // Ayarlar (AI & Privacy)
        var settings = new SettingsViewModel(shell) { Tab = SettingsTab.Ai };
        _ = shell.ShowDialogAsync(settings);
        Pump(500);
        Capture(window, "09-settings-ai-privacy");
        settings.CancelCommand.Execute(null);
        Pump(200);

        // Restore planı
        var undoable = repo.Session.GetUndoState().Undoable;
        if (undoable is not null)
        {
            var plan = Wait(repo.Session.PlanUndoAsync(undoable));
            var planDialog = new RestorePlanDialogViewModel(plan, isPro: true, confirmText: Loc.T("Undo", "Geri al"));
            _ = shell.ShowDialogAsync(planDialog);
            Pump(500);
            Capture(window, "10-undo-plan");
            planDialog.CancelCommand.Execute(null);
        }

        Wait(services.DisposeAsync().AsTask());
        Console.WriteLine("Screenshots written to " + _output);
        try
        {
            Directory.Delete(workRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        return 0;
    }

    /// <summary>Uygulamanın kendi işlem hattıyla recovery point ve snapshot üretir.</summary>
    private static void SeedHistory(RepositoryViewModel repo, string repoPath)
    {
        var actions = repo.Session.Actions;
        File.WriteAllText(Path.Combine(repoPath, "src", "Auth", "TokenService.cs"), DemoRepository.TokenServiceV3);
        Wait(repo.Session.TimeMachine.SaveSnapshotNowAsync("Before trying a new token cache"));
        Thread.Sleep(1100);
        File.AppendAllText(Path.Combine(repoPath, "src", "Auth", "TokenService.cs"), "\n// experiment: sliding expiration\n");
        Wait(actions.ResetAsync("HEAD", ResetMode.Hard, "main"));
        Thread.Sleep(1100);
        var undo = repo.Session.GetUndoState().Undoable!;
        Wait(repo.Session.ExecutePlanAsync(Wait(repo.Session.PlanUndoAsync(undo)), undo));
        Thread.Sleep(1100);
        var branch = repo.Refs.FirstOrDefault(r => r.Name == "experiment/cache");
        if (branch is not null) Wait(actions.DeleteBranchAsync(branch));
        Thread.Sleep(1100);
        File.WriteAllText(Path.Combine(repoPath, "src", "Web", "LoginPage.razor"), DemoRepository.LoginPageV2);
        Wait(repo.Session.TimeMachine.SaveSnapshotNowAsync(null));
        Git(repoPath, "add", "src/Web/LoginPage.razor");
        File.WriteAllText(Path.Combine(repoPath, "docs", "notes.md"), "# Notes\n\n- try remember-me checkbox\n");
    }

    private static void Capture(Avalonia.Controls.Window window, string name)
    {
        Pump(100);
        using var frame = window.CaptureRenderedFrame();
        var path = Path.Combine(_output, name + ".png");
        frame!.Save(path);
        Console.WriteLine("  " + path);
    }

    private static void Pump(int milliseconds)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < milliseconds)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(10);
        }
    }

    private static void Wait(Task task)
    {
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        task.GetAwaiter().GetResult();
    }

    private static T Wait<T>(Task<T> task)
    {
        Wait((Task)task);
        return task.Result;
    }

    internal static void Git(string repo, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        process.WaitForExit();
    }
}
