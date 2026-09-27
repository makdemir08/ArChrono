namespace ArChrono.Application.Repositories;

[Flags]
public enum RepositoryChange
{
    None = 0,
    WorkingTree = 1,
    Index = 2,
    Refs = 4,
    State = 8,
    All = WorkingTree | Index | Refs | State,
}

/// <summary>Çalışma alanını ve .git meta verisini izler; olayları debounce ederek tek bildirimde toplar.</summary>
public sealed class RepositoryWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Timer _debounce;
    private readonly TimeSpan _delay;
    private readonly string _root;
    private readonly string _gitDir;
    private readonly string _commonDir;
    private int _pending;

    public RepositoryWatcher(string root, string gitDir, string commonDir, TimeSpan? debounce = null)
    {
        _root = Normalize(root);
        _gitDir = Normalize(gitDir);
        _commonDir = Normalize(commonDir);
        _delay = debounce ?? TimeSpan.FromMilliseconds(400);
        _debounce = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

        foreach (var directory in new[] { _root, _gitDir, _commonDir }.Distinct(StringComparer.Ordinal))
        {
            if (!Directory.Exists(directory)) continue;
            if (directory != _root && directory.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Changed += OnEvent;
            watcher.Created += OnEvent;
            watcher.Deleted += OnEvent;
            watcher.Renamed += (s, e) => OnEvent(s, e);
            watcher.Error += (_, _) => Raise(RepositoryChange.All);
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    public event EventHandler<RepositoryChange>? Changed;

    public bool IsEnabled { get; set; } = true;

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        var change = Classify(Normalize(e.FullPath));
        if (change != RepositoryChange.None) Raise(change);
    }

    internal RepositoryChange Classify(string path)
    {
        if (path.Contains(".archrono-", StringComparison.Ordinal)) return RepositoryChange.None;

        string? relative = null;
        if (IsUnder(path, _gitDir)) relative = path[(_gitDir.Length + 1)..];
        else if (IsUnder(path, _commonDir)) relative = path[(_commonDir.Length + 1)..];

        if (relative is null)
            return IsUnder(path, _root) || path == _root ? RepositoryChange.WorkingTree : RepositoryChange.None;

        relative = relative.Replace('\\', '/');
        if (relative.EndsWith(".lock", StringComparison.Ordinal) || relative.StartsWith("objects/", StringComparison.Ordinal)
            || relative.StartsWith("archrono/", StringComparison.Ordinal) || relative.StartsWith("logs/refs/archrono", StringComparison.Ordinal)
            || relative.StartsWith("refs/archrono", StringComparison.Ordinal) || relative is "FETCH_HEAD" or "ORIG_HEAD" or "gc.log")
            return RepositoryChange.None;
        if (relative == "index") return RepositoryChange.Index;
        if (relative is "HEAD" or "packed-refs" || relative.StartsWith("refs/", StringComparison.Ordinal) || relative.StartsWith("logs/", StringComparison.Ordinal))
            return RepositoryChange.Refs;
        if (relative is "MERGE_HEAD" or "CHERRY_PICK_HEAD" or "REVERT_HEAD" or "BISECT_LOG" || relative.StartsWith("rebase-", StringComparison.Ordinal))
            return RepositoryChange.State;
        return RepositoryChange.None;
    }

    private void Raise(RepositoryChange change)
    {
        if (!IsEnabled) return;
        int current, updated;
        do
        {
            current = _pending;
            updated = current | (int)change;
        } while (Interlocked.CompareExchange(ref _pending, updated, current) != current);
        _debounce.Change(_delay, Timeout.InfiniteTimeSpan);
    }

    private void Flush()
    {
        var change = (RepositoryChange)Interlocked.Exchange(ref _pending, 0);
        if (change != RepositoryChange.None) Changed?.Invoke(this, change);
    }

    private static bool IsUnder(string path, string directory) =>
        path.Length > directory.Length && path.StartsWith(directory, StringComparison.Ordinal) && path[directory.Length] == Path.DirectorySeparatorChar;

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
        _debounce.Dispose();
    }
}
