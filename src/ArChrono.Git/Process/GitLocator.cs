using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ArChrono.Git.Process;

public sealed record GitExecutable(string Path, Version Version, string VersionText)
{
    public static readonly Version MinimumVersion = new(2, 38);

    /// <summary><c>git merge-tree --write-tree</c> (çalışma alanına dokunmadan conflict tahmini).</summary>
    public bool SupportsMergeTreeWriteTree => Version >= new Version(2, 38);

    public bool IsSupported => Version >= MinimumVersion;
}

/// <summary>Sistemdeki git executable'ını bulur ve sürümünü doğrular.</summary>
public static partial class GitLocator
{
    [GeneratedRegex(@"git version (\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public static GitExecutable? Find(string? preferredPath = null)
    {
        foreach (var candidate in Candidates(preferredPath))
        {
            if (!File.Exists(candidate)) continue;
            var executable = Probe(candidate);
            if (executable is not null) return executable;
        }
        return null;
    }

    public static GitExecutable? Probe(string path)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new ProcessStartInfo(path)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add("--version");
            process.StartInfo.Environment["LC_ALL"] = "C";
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return process.ExitCode == 0 ? ParseVersion(path, output) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    internal static GitExecutable? ParseVersion(string path, string versionOutput)
    {
        var match = VersionPattern().Match(versionOutput);
        if (!match.Success) return null;
        var version = new Version(
            int.Parse(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value),
            match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0);
        return new GitExecutable(path, version, versionOutput.Trim());
    }

    private static IEnumerable<string> Candidates(string? preferredPath)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath)) yield return preferredPath;

        var fromEnvironment = Environment.GetEnvironmentVariable("ARCHRONO_GIT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) yield return fromEnvironment;

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var executableName = isWindows ? "git.exe" : "git";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Homebrew git genellikle Apple Git'ten daha günceldir.
            yield return "/opt/homebrew/bin/git";
            yield return "/usr/local/bin/git";
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = System.IO.Path.Combine(directory.Trim('"'), executableName);
            // /usr/bin/git, Xcode Command Line Tools yoksa kurulum diyaloğu açan bir shim'dir.
            if (candidate == "/usr/bin/git" && !HasAppleCommandLineTools()) continue;
            yield return candidate;
        }

        if (isWindows)
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                     })
            {
                if (string.IsNullOrEmpty(root)) continue;
                yield return System.IO.Path.Combine(root, "Git", "cmd", "git.exe");
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && HasAppleCommandLineTools())
        {
            yield return "/usr/bin/git";
        }
    }

    private static bool? _hasCommandLineTools;

    private static bool HasAppleCommandLineTools()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return true;
        return _hasCommandLineTools ??=
            File.Exists("/Library/Developer/CommandLineTools/usr/bin/git") ||
            Directory.Exists("/Applications/Xcode.app/Contents/Developer/usr/bin");
    }
}
