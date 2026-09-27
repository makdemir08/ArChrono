using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ArChrono.Recovery.Content;

public sealed record StoredBlob(string BlobId, long Size, long StoredSize, string Compression, bool IsNew);

/// <summary>
/// Uygulamaya ait content-addressed store (ADR-0003). Anahtar Git uyumlu blob id'sidir;
/// içerik Brotli ile sıkıştırılır, yazma atomiktir, okuma sırasında bütünlük doğrulanır.
/// </summary>
public sealed class ContentStore
{
    private static readonly byte[] Magic = "ACB1"u8.ToArray();
    private const int HeaderLength = 8;
    private const byte CompressionNone = 0;
    private const byte CompressionBrotli = 1;

    private static readonly HashSet<string> CompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".heic", ".avif", ".ico",
        ".zip", ".gz", ".tgz", ".bz2", ".xz", ".7z", ".rar", ".zst", ".br", ".lz4",
        ".jar", ".apk", ".aab", ".ipa", ".nupkg", ".docx", ".xlsx", ".pptx",
        ".mp3", ".mp4", ".m4a", ".mov", ".avi", ".mkv", ".webm", ".ogg", ".flac",
        ".woff", ".woff2", ".pdf",
    };

    public ContentStore(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        ObjectsDirectory = Path.Combine(RootDirectory, "objects");
        TempDirectory = Path.Combine(RootDirectory, "tmp");
        Directory.CreateDirectory(ObjectsDirectory);
        Directory.CreateDirectory(TempDirectory);
    }

    public string RootDirectory { get; }
    public string ObjectsDirectory { get; }
    public string TempDirectory { get; }

    /// <summary>Snapshot yazımı ile çöp toplama arasında eşzamanlılık kilidi.</summary>
    public SemaphoreSlim MutationLock { get; } = new(1, 1);

    /// <summary>Git'in blob id hesaplaması: hash("blob &lt;len&gt;\0" + içerik).</summary>
    public static string ComputeBlobId(ReadOnlySpan<byte> content, string objectFormat = "sha1")
    {
        var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        using var hash = IncrementalHash.CreateHash(objectFormat == "sha256" ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1);
        hash.AppendData(header);
        hash.AppendData(content);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public string PathFor(string blobId) => Path.Combine(ObjectsDirectory, blobId[..2], blobId[2..]);

    public bool Contains(string blobId) => File.Exists(PathFor(blobId));

    public StoredBlob Put(ReadOnlySpan<byte> content, string objectFormat, string? pathHint = null)
    {
        var blobId = ComputeBlobId(content, objectFormat);
        var target = PathFor(blobId);
        if (File.Exists(target))
            return new StoredBlob(blobId, content.Length, new FileInfo(target).Length, ReadCompression(target), IsNew: false);

        var (body, compression) = Encode(content, pathHint);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = Path.Combine(TempDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920))
            {
                Span<byte> header = stackalloc byte[HeaderLength];
                Magic.CopyTo(header);
                header[4] = compression;
                stream.Write(header);
                stream.Write(body.Span);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, target, overwrite: false);
        }
        catch (IOException) when (File.Exists(target))
        {
            // Aynı içerik eşzamanlı yazıldı; mevcut dosya geçerlidir.
            TryDelete(temp);
        }
        finally
        {
            body.Dispose();
        }

        var storedSize = new FileInfo(target).Length;
        return new StoredBlob(blobId, content.Length, storedSize, compression == CompressionBrotli ? "brotli" : "none", IsNew: true);
    }

    public byte[] Get(string blobId, string objectFormat = "sha1") =>
        TryGet(blobId, objectFormat, out var content) ? content : throw new FileNotFoundException($"Content {blobId} is not in the store.");

    public bool TryGet(string blobId, string objectFormat, out byte[] content)
    {
        content = [];
        var path = PathFor(blobId);
        if (!File.Exists(path)) return false;

        var raw = File.ReadAllBytes(path);
        if (raw.Length < HeaderLength || !raw.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException($"Content {blobId} is corrupted (bad header).");

        content = raw[4] switch
        {
            CompressionNone => raw[HeaderLength..],
            CompressionBrotli => Decompress(raw.AsSpan(HeaderLength)),
            _ => throw new InvalidDataException($"Content {blobId} uses an unknown compression."),
        };

        if (ComputeBlobId(content, objectFormat) != blobId)
            throw new InvalidDataException($"Content {blobId} is corrupted (hash mismatch).");
        return true;
    }

    public void Delete(string blobId) => TryDelete(PathFor(blobId));

    public DateTime GetWriteTimeUtc(string blobId) => File.GetLastWriteTimeUtc(PathFor(blobId));

    /// <summary>Yarım kalmış yazımlardan kalan geçici dosyaları siler.</summary>
    public int CleanTemporaryFiles(TimeSpan olderThan)
    {
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(TempDirectory))
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < olderThan) continue;
            TryDelete(file);
            removed++;
        }
        return removed;
    }

    public IEnumerable<string> EnumerateBlobIds()
    {
        foreach (var directory in Directory.EnumerateDirectories(ObjectsDirectory))
        {
            var prefix = Path.GetFileName(directory);
            foreach (var file in Directory.EnumerateFiles(directory)) yield return prefix + Path.GetFileName(file);
        }
    }

    private static (MemoryBody Body, byte Compression) Encode(ReadOnlySpan<byte> content, string? pathHint)
    {
        var skip = content.Length < 64 || (pathHint is not null && CompressedExtensions.Contains(Path.GetExtension(pathHint)));
        if (!skip)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(BrotliEncoder.GetMaxCompressedLength(content.Length));
            if (BrotliEncoder.TryCompress(content, buffer, out var written, quality: 5, window: 22) && written < content.Length * 0.9)
                return (new MemoryBody(buffer, written, pooled: true), CompressionBrotli);
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return (new MemoryBody(content.ToArray(), content.Length, pooled: false), CompressionNone);
    }

    private static byte[] Decompress(ReadOnlySpan<byte> compressed)
    {
        using var input = new MemoryStream(compressed.ToArray());
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }

    private static string ReadCompression(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[HeaderLength];
        return stream.Read(header) == HeaderLength && header[4] == CompressionBrotli ? "brotli" : "none";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private readonly struct MemoryBody(byte[] buffer, int length, bool pooled) : IDisposable
    {
        public ReadOnlySpan<byte> Span => buffer.AsSpan(0, length);

        public void Dispose()
        {
            if (pooled) ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
