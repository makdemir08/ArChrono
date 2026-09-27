using System.Text;

namespace ArChrono.Recovery.Snapshots;

/// <summary>Çalışma alanı dosyalarını Git semantiğiyle (mode, symlink) okuma ve yazma.</summary>
internal static class WorkingTreeFiles
{
    public const int ModeRegular = 0x81A4;    // 100644
    public const int ModeExecutable = 0x81ED; // 100755
    public const int ModeSymlink = 0xA000;    // 120000

    public readonly record struct FileContent(byte[] Bytes, int Mode);

    public enum ReadStatus
    {
        Ok,
        Missing,
        TooLarge,
        Unreadable,
    }

    public static (ReadStatus Status, FileContent Content, string? Error) Read(string fullPath, long maxBytes, int fallbackMode)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (info.LinkTarget is { } target)
                return (ReadStatus.Ok, new FileContent(Encoding.UTF8.GetBytes(target.Replace('\\', '/')), ModeSymlink), null);
            if (!info.Exists)
                return (ReadStatus.Missing, default, null);
            if (info.Length > maxBytes)
                return (ReadStatus.TooLarge, default, $"larger than {maxBytes / (1024 * 1024)} MB");

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var before = (info.Length, info.LastWriteTimeUtc);
                byte[] bytes;
                using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    bytes = new byte[stream.Length];
                    stream.ReadExactly(bytes);
                }
                info.Refresh();
                if (before == (info.Length, info.LastWriteTimeUtc) || attempt == 1)
                    return (ReadStatus.Ok, new FileContent(bytes, DetectMode(fullPath, fallbackMode)), null);
            }
            return (ReadStatus.Unreadable, default, "file kept changing while it was read");
        }
        catch (FileNotFoundException)
        {
            return (ReadStatus.Missing, default, null);
        }
        catch (DirectoryNotFoundException)
        {
            return (ReadStatus.Missing, default, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (ReadStatus.Unreadable, default, ex.Message);
        }
    }

    private static int DetectMode(string fullPath, int fallbackMode)
    {
        if (OperatingSystem.IsWindows())
            return fallbackMode is ModeExecutable or ModeRegular ? fallbackMode : ModeRegular;
        var mode = File.GetUnixFileMode(fullPath);
        return (mode & UnixFileMode.UserExecute) != 0 ? ModeExecutable : ModeRegular;
    }

    /// <summary>İçeriği atomik olarak yazar; içerik ve mode zaten aynıysa dokunmaz. Yazıldıysa true.</summary>
    public static bool Write(string fullPath, byte[] content, int mode)
    {
        var directory = Path.GetDirectoryName(fullPath)!;
        if (File.Exists(directory))
            File.Delete(directory); // eskiden dosya olan yerde klasör gerekiyor
        Directory.CreateDirectory(directory);

        if (Directory.Exists(fullPath) && new FileInfo(fullPath).LinkTarget is null)
            Directory.Delete(fullPath, recursive: true);

        if (mode == ModeSymlink)
        {
            var target = Encoding.UTF8.GetString(content);
            var existing = new FileInfo(fullPath);
            if (existing.LinkTarget == target) return false;
            if (existing.Exists || existing.LinkTarget is not null) File.Delete(fullPath);
            try
            {
                File.CreateSymbolicLink(fullPath, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Symlink oluşturulamayan platformlarda (Windows, yetkisiz) Git gibi düz dosya yaz.
                File.WriteAllBytes(fullPath, content);
            }
            return true;
        }

        var current = new FileInfo(fullPath);
        if (current.LinkTarget is not null)
        {
            File.Delete(fullPath);
        }
        else if (current.Exists && current.Length == content.Length)
        {
            var same = File.ReadAllBytes(fullPath).AsSpan().SequenceEqual(content);
            if (same)
            {
                ApplyMode(fullPath, mode);
                return false;
            }
        }

        var temp = fullPath + ".archrono-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllBytes(temp, content);
        ApplyMode(temp, mode);
        File.Move(temp, fullPath, overwrite: true);
        return true;
    }

    public static bool Delete(string fullPath, string rootPath)
    {
        var info = new FileInfo(fullPath);
        if (!info.Exists && info.LinkTarget is null) return false;
        File.Delete(fullPath);
        PruneEmptyDirectories(Path.GetDirectoryName(fullPath), rootPath);
        return true;
    }

    public static void PruneEmptyDirectories(string? directory, string rootPath)
    {
        var root = rootPath.TrimEnd(Path.DirectorySeparatorChar);
        while (directory is not null && directory.Length > root.Length &&
               directory.StartsWith(root, StringComparison.Ordinal) && Directory.Exists(directory) &&
               !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }

    private static void ApplyMode(string path, int mode)
    {
        if (OperatingSystem.IsWindows()) return;
        var current = File.GetUnixFileMode(path);
        var executable = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        var desired = mode == ModeExecutable ? current | executable : current & ~executable;
        if (desired != current) File.SetUnixFileMode(path, desired);
    }
}
