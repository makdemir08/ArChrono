using System.Collections.Concurrent;
using ArChrono.Application.Ai;
using ArChrono.Application.Repositories;
using ArChrono.Application.Settings;
using ArChrono.Git;
using ArChrono.Git.Errors;
using ArChrono.Git.Process;
using ArChrono.Localization;
using ArChrono.Platform;
using ArChrono.Platform.Credentials;
using ArChrono.Recovery;
using ArChrono.Recovery.Snapshots;
using ArChrono.Storage.Records;
using ArChrono.Storage.Stores;

namespace ArChrono.Application;

/// <summary>Git Console: uygulamanın çalıştırdığı kullanıcıya görünür git komutları (iç komutlar hariç).</summary>
public sealed class GitConsoleLog
{
    private const int Capacity = 500;
    private readonly ConcurrentQueue<GitCommandEventArgs> _entries = new();

    public event EventHandler<GitCommandEventArgs>? EntryAdded;

    public IReadOnlyList<GitCommandEventArgs> Entries => _entries.ToArray();

    internal void Attach(IGitRunner runner) => runner.CommandCompleted += (sender, e) =>
    {
        if (e.Command.IsInternal) return;
        _entries.Enqueue(e);
        while (_entries.Count > Capacity) _entries.TryDequeue(out _);
        EntryAdded?.Invoke(this, e);
    };
}

/// <summary>
/// Composition root. Tüm servisler açık bağımlılıklarla burada kurulur (DI container yok, ADR-0001).
/// UI bu nesneyi tek giriş noktası olarak kullanır.
/// </summary>
public sealed class AppServices : IAsyncDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly List<RepositorySession> _sessions = [];
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _retentionLoop;

    private AppServices(AppPaths paths, ArChronoStorage storage)
    {
        Paths = paths;
        Storage = storage;
        Settings = new SettingsService(storage.Settings);
        Credentials = CredentialStoreFactory.Create();
        Recovery = new RecoveryEngine(storage, paths.ContentStoreDirectory, ToRecoveryOptions(Settings.Current));
        Ai = new AiAssistant(Settings, Credentials, storage, _http);
        Settings.Changed += (_, s) => Recovery.Options = ToRecoveryOptions(s);
        DetectGit();
    }

    public AppPaths Paths { get; }
    public ArChronoStorage Storage { get; }
    public SettingsService Settings { get; }
    public ICredentialStore Credentials { get; }
    public RecoveryEngine Recovery { get; }
    public AiAssistant Ai { get; }
    public GitConsoleLog Console { get; } = new();

    public GitExecutable? Git { get; private set; }
    public IGitRunner? Runner { get; private set; }

    public IReadOnlyList<RepositorySession> Sessions => _sessions;

    public static AppServices Create(AppPaths? paths = null)
    {
        paths ??= AppPaths.ForCurrentUser();
        Log.Initialize(paths);
        var storage = ArChronoStorage.OpenAndMigrate(paths.DatabasePath);
        return new AppServices(paths, storage);
    }

    /// <summary>Git'i (ayarlardaki yol öncelikli) bulur. Bulunamazsa UI kurulum rehberi gösterir.</summary>
    public GitExecutable? DetectGit()
    {
        Git = GitLocator.Find(Settings.Current.Git.ExecutablePath);
        if (Git is not null)
        {
            var runner = new GitProcessRunner(Git);
            Console.Attach(runner);
            Runner = runner;
        }
        return Git;
    }

    public IGitRunner RequireRunner() =>
        Runner ?? throw new GitException(GitError.Simple(GitErrorCode.GitNotFound,
            Loc.T("Git is not installed.", "Git kurulu değil."), Loc.T("ArChrono uses the Git installed on your computer.", "ArChrono bilgisayarınızda kurulu Git'i kullanır."),
            OperatingSystem.IsWindows() ? Loc.T("Install Git for Windows from git-scm.com, then restart ArChrono.", "git-scm.com adresinden Git for Windows'u kurun, sonra ArChrono'yu yeniden başlatın.")
            : OperatingSystem.IsMacOS() ? Loc.T("Install the Xcode Command Line Tools (xcode-select --install) or Git from Homebrew.", "Xcode Command Line Tools'u (xcode-select --install) veya Homebrew'dan Git'i kurun.")
            : Loc.T("Install git with your package manager.", "Git'i paket yöneticinizle kurun.")));

    public GitClient CreateClient() => new(RequireRunner());

    public IReadOnlyList<RepositoryRecord> GetRecentRepositories() =>
        Storage.Repositories.GetRecent().Where(r => Directory.Exists(r.RootPath)).ToList();

    public async Task<RepositorySession> OpenSessionAsync(string path, CancellationToken cancellationToken = default)
    {
        var git = await GitRepository.OpenAsync(RequireRunner(), path, cancellationToken).ConfigureAwait(false);
        var existing = _sessions.FirstOrDefault(s => s.RootPath == git.Info.RootPath);
        if (existing is not null) return existing;

        var record = Storage.Repositories.Upsert(git.Info.RootPath, git.Info.GitDir, git.Info.CommonDir, git.Info.Name, git.Info.ObjectFormat);
        var session = new RepositorySession(git, record, Recovery, Settings);
        _sessions.Add(session);
        EnsureRetentionLoop();
        Log.Info($"Opened repository {git.Info.Name}");
        return session;
    }

    public async Task CloseSessionAsync(RepositorySession session)
    {
        _sessions.Remove(session);
        await session.DisposeAsync().ConfigureAwait(false);
    }

    public async Task<RepositorySession> CloneAsync(string url, string destination, IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        var repository = await CreateClient().CloneAsync(url, destination, progress, cancellationToken).ConfigureAwait(false);
        return await OpenSessionAsync(repository.Info.RootPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RepositorySession> InitAsync(string path, CancellationToken cancellationToken = default)
    {
        var repository = await CreateClient().InitAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await OpenSessionAsync(repository.Info.RootPath, cancellationToken).ConfigureAwait(false);
    }

    public void RemoveFromRecent(long repositoryId) => Storage.Repositories.Remove(repositoryId);

    private void EnsureRetentionLoop()
    {
        _retentionLoop ??= Task.Run(async () =>
        {
            var token = _lifetime.Token;
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var report = await Recovery.Retention.RunAsync(Settings.Current.TimeMachine.ToPolicy(), OpenForMaintenanceAsync, token).ConfigureAwait(false);
                        if (report.DeletedSnapshots + report.DeletedRecoveryPoints > 0)
                            Log.Info($"Retention: {report.DeletedSnapshots} snapshots, {report.DeletedRecoveryPoints} points, {report.FreedBytes} bytes freed.");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.Error("Retention failed", ex);
                    }
                    await Task.Delay(TimeSpan.FromHours(1), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private async Task<GitRepository?> OpenForMaintenanceAsync(RepositoryRecord record)
    {
        if (Runner is null || !Directory.Exists(record.RootPath)) return null;
        try
        {
            return await GitRepository.OpenAsync(Runner, record.RootPath).ConfigureAwait(false);
        }
        catch (GitException)
        {
            return null;
        }
    }

    private static RecoveryOptions ToRecoveryOptions(AppSettings settings) => new()
    {
        PinInRepository = settings.TimeMachine.PinInRepository,
        Snapshots = new SnapshotOptions { MaxFileSizeBytes = settings.TimeMachine.MaxFileSizeMegabytes * 1024L * 1024L },
    };

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        foreach (var session in _sessions.ToList()) await session.DisposeAsync().ConfigureAwait(false);
        _sessions.Clear();
        _http.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
