namespace ArChrono.Platform;

/// <summary>Uygulama verisinin platforma göre konumları (architecture/overview.md).</summary>
public sealed record AppPaths(string DataDirectory, string LogDirectory)
{
    public string DatabasePath => Path.Combine(DataDirectory, "archrono.db");

    public string ContentStoreDirectory => Path.Combine(DataDirectory, "store");

    public static AppPaths ForCurrentUser()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("ARCHRONO_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            return Create(overrideDirectory, Path.Combine(overrideDirectory, "logs"));

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Create(Path.Combine(home, "Library", "Application Support", "ArChrono"), Path.Combine(home, "Library", "Logs", "ArChrono"));

        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Create(Path.Combine(local, "ArChrono"), Path.Combine(local, "ArChrono", "logs"));
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var data = Path.Combine(string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".local", "share") : xdg, "archrono");
        return Create(data, Path.Combine(data, "logs"));
    }

    private static AppPaths Create(string data, string logs)
    {
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(logs);
        return new AppPaths(data, logs);
    }
}

/// <summary>Basit dosya log'u. Repository içeriği veya sır yazılmaz.</summary>
public static partial class Log
{
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<scheme>[a-zA-Z][a-zA-Z0-9+.-]*://)[^/@\s]+@")]
    private static partial System.Text.RegularExpressions.Regex UrlCredentials();

    private static readonly object Gate = new();
    private static string? _file;

    public static void Initialize(AppPaths paths) => _file = Path.Combine(paths.LogDirectory, $"archrono-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        var line = $"{DateTimeOffset.Now:O} [{level}] {UrlCredentials().Replace(message, "${scheme}***@")}{(exception is null ? "" : Environment.NewLine + exception)}";
        System.Diagnostics.Debug.WriteLine(line);
        if (_file is null) return;
        lock (Gate)
        {
            try
            {
                File.AppendAllText(_file, line + Environment.NewLine);
            }
            catch (IOException)
            {
            }
        }
    }
}
