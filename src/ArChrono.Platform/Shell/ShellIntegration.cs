using System.Diagnostics;

namespace ArChrono.Platform.Shell;

/// <summary>Sistem terminali, dosya yöneticisi ve varsayılan uygulamalarla entegrasyon (ADR-0008).</summary>
public static class ShellIntegration
{
    /// <param name="preferredTerminal">macOS: "Terminal", "iTerm", "Warp", "Ghostty"; Windows: "wt", "powershell", "cmd".</param>
    public static void OpenTerminal(string directory, string? preferredTerminal = null)
    {
        if (OperatingSystem.IsMacOS())
        {
            Start("open", "-a", string.IsNullOrWhiteSpace(preferredTerminal) ? "Terminal" : preferredTerminal!, directory);
        }
        else if (OperatingSystem.IsWindows())
        {
            var terminal = preferredTerminal?.ToLowerInvariant();
            if (terminal is null or "wt" && TryStart("wt.exe", "-d", directory)) return;
            if (terminal == "cmd")
                Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = directory, UseShellExecute = true });
            else
                Start(new ProcessStartInfo("powershell.exe", "-NoExit") { WorkingDirectory = directory, UseShellExecute = true });
        }
        else
        {
            foreach (var candidate in new[] { preferredTerminal, "x-terminal-emulator", "gnome-terminal", "konsole", "xterm" })
            {
                if (candidate is null) continue;
                if (TryStart(new ProcessStartInfo(candidate) { WorkingDirectory = directory, UseShellExecute = false })) return;
            }
        }
    }

    public static void RevealInFileManager(string path)
    {
        if (OperatingSystem.IsMacOS()) Start("open", "-R", path);
        else if (OperatingSystem.IsWindows()) Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        else Start("xdg-open", Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path);
    }

    public static void OpenWithDefaultApplication(string path)
    {
        if (OperatingSystem.IsMacOS()) Start("open", path);
        else if (OperatingSystem.IsWindows()) Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else Start("xdg-open", path);
    }

    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return;
        OpenWithDefaultApplication(uri.ToString());
    }

    private static void Start(string file, params string[] arguments)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        Start(info);
    }

    private static void Start(ProcessStartInfo info)
    {
        using var process = Process.Start(info);
    }

    private static bool TryStart(string file, params string[] arguments)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return TryStart(info);
    }

    private static bool TryStart(ProcessStartInfo info)
    {
        try
        {
            using var process = Process.Start(info);
            return process is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
