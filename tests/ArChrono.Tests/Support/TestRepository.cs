using System.Diagnostics;
using System.Text;
using ArChrono.Git;
using ArChrono.Git.Process;

namespace ArChrono.Tests.Support;

public static class TestGit
{
    private static readonly Lazy<GitProcessRunner> LazyRunner = new(() =>
    {
        var executable = GitLocator.Find() ?? throw new InvalidOperationException("git executable not found for tests.");
        return new GitProcessRunner(executable);
    });

    public static GitProcessRunner Runner => LazyRunner.Value;

    public static string CreateTempDirectory(string prefix = "repo")
    {
        var path = Path.Combine(Path.GetTempPath(), "archrono-tests", prefix + "-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(path);
        // macOS'ta /var → /private/var; git gerçek yolu döndürür.
        return Path.GetFullPath(new DirectoryInfo(path).ResolveLinkTarget(true)?.FullName ?? RealPath(path));
    }

    private static string RealPath(string path)
    {
        if (!OperatingSystem.IsMacOS()) return path;
        return path.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + path : path;
    }

    public static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch (IOException)
            {
            }
        }
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Testler için geçici Git repository'si. Git'e doğrudan (adapter dışından) erişerek durum hazırlar.</summary>
public sealed class TestRepository : IDisposable
{
    private int _tick;

    private TestRepository(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public static TestRepository Create(bool withInitialCommit = true)
    {
        var repo = new TestRepository(TestGit.CreateTempDirectory());
        repo.Git("init", "--initial-branch=main");
        repo.Git("config", "user.name", "Test User");
        repo.Git("config", "user.email", "test@example.com");
        repo.Git("config", "commit.gpgsign", "false");
        repo.Git("config", "core.autocrlf", "false");
        if (withInitialCommit)
        {
            repo.WriteFile("README.md", "# Test\n");
            repo.CommitAll("Initial commit");
        }
        return repo;
    }

    public async Task<GitRepository> OpenAsync() => await GitRepository.OpenAsync(TestGit.Runner, Root);

    public string Git(params string[] args) => GitIn(Root, args);

    public string GitIn(string workingDirectory, params string[] args)
    {
        var info = new ProcessStartInfo(TestGit.Runner.Executable.Path)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["LC_ALL"] = "C";
        info.Environment["GIT_EDITOR"] = "true";
        // Commit tarihleri deterministik ve artan olsun.
        var date = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000 + _tick++ * 60).ToString("yyyy-MM-ddTHH:mm:ss+00:00");
        info.Environment["GIT_AUTHOR_DATE"] = date;
        info.Environment["GIT_COMMITTER_DATE"] = date;

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({process.ExitCode}):\n{stderr}\n{stdout}");
        return stdout;
    }

    public string FullPath(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public void WriteFile(string relative, string content)
    {
        var full = FullPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
    }

    public string ReadFile(string relative) => File.ReadAllText(FullPath(relative));

    public bool FileExists(string relative) => File.Exists(FullPath(relative));

    public void DeleteFile(string relative) => File.Delete(FullPath(relative));

    public string CommitAll(string message)
    {
        Git("add", "-A");
        Git("commit", "-q", "-m", message);
        return Head();
    }

    public string Head() => Git("rev-parse", "HEAD").Trim();

    public string RevParse(string revision) => Git("rev-parse", revision).Trim();

    public string CurrentBranch() => Git("rev-parse", "--abbrev-ref", "HEAD").Trim();

    public void Dispose() => TestGit.DeleteDirectory(Root);
}
