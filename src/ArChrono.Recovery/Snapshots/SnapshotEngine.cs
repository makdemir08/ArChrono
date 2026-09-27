using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArChrono.Git;
using ArChrono.Git.Models;
using ArChrono.Recovery.Content;
using ArChrono.Storage.Records;
using ArChrono.Storage.Stores;

namespace ArChrono.Recovery.Snapshots;

public sealed record SnapshotOptions
{
    public long MaxFileSizeBytes { get; init; } = 25L * 1024 * 1024;

    /// <summary>Tek snapshot'ta saklanacak en fazla overlay dosyası (ör. yanlışlıkla ignore edilmemiş devasa klasörler).</summary>
    public int MaxOverlayFiles { get; init; } = 5_000;

    /// <summary>Tek snapshot'ta saklanacak en fazla ham içerik.</summary>
    public long MaxOverlayBytes { get; init; } = 512L * 1024 * 1024;
}

public sealed record SkippedFile(string Path, string Reason);

public sealed record SnapshotResult(SnapshotRecord Snapshot, bool Unchanged, IReadOnlyList<SkippedFile> Skipped);

/// <summary>
/// Çalışma alanının (HEAD + index + overlay) snapshot'ını alır.
/// Değişiklik tespiti Git'in stat cache'i ile yapılır; yalnızca değişen dosyalar okunur.
/// </summary>
public sealed class SnapshotEngine(ArChronoStorage storage, ContentStore store, PinManager pins, Func<SnapshotOptions> options)
{
    public SnapshotOptions Options => options();

    public async Task<SnapshotResult> CaptureAsync(GitRepository repository, long repositoryId, SnapshotTrigger trigger, string? label = null,
        bool force = false, CancellationToken cancellationToken = default)
    {
        var status = await repository.Status.GetStatusAsync(cancellationToken, isInternal: true).ConfigureAwait(false);
        var headSha = status.Branch.HeadSha;
        var headRef = status.Branch.BranchName is { } branch ? RefNames.Branch(branch) : null;

        var indexTree = await repository.Objects.WriteTreeFromIndexCopyAsync(cancellationToken).ConfigureAwait(false);
        var hasUnmergedIndex = indexTree is null;
        indexTree ??= headSha is not null
            ? await repository.Refs.ResolveObjectAsync(headSha + "^{tree}", cancellationToken).ConfigureAwait(false)
            : null;
        indexTree ??= await repository.Objects.GetEmptyTreeAsync(cancellationToken).ConfigureAwait(false);

        await store.MutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (entries, blobs, skipped, totalBytes, newBytes) = CaptureOverlay(repository, status, hasUnmergedIndex, cancellationToken);
            var fingerprint = ComputeFingerprint(headSha, indexTree, entries);

            var latest = storage.Snapshots.GetLatest(repositoryId);
            if (!force && latest is not null && latest.Fingerprint == fingerprint)
                return new SnapshotResult(latest, Unchanged: true, skipped);

            string? pinCommit = null;
            // HEAD ve index nesneleri refs/archrono/pins/* ile gc'ye karşı korunur (ADR-0003).
            if (pins.IsEnabled)
            {
                pinCommit = storage.Snapshots.FindPin(repositoryId, headSha, indexTree);
                if (pinCommit is null)
                {
                    pinCommit = await repository.Objects.CommitTreeAsync(indexTree, headSha is null ? [] : [headSha],
                        $"ArChrono snapshot pin\n\nindex {indexTree}\nhead {headSha ?? "(unborn)"}\n", cancellationToken).ConfigureAwait(false);
                    await pins.EnsurePinnedAsync(repository, [pinCommit], cancellationToken).ConfigureAwait(false);
                }
            }

            var record = new SnapshotRecord(0, repositoryId, DateTimeOffset.Now, trigger, label, headRef, headSha, indexTree, pinCommit,
                fingerprint, entries.Count, totalBytes, newBytes,
                skipped.Count == 0 ? null : JsonSerializer.Serialize(skipped));
            var saved = storage.Snapshots.Insert(record, entries, blobs, pinCommit is null ? null : repository.Info.CommonDir);
            return new SnapshotResult(saved, Unchanged: false, skipped);
        }
        finally
        {
            store.MutationLock.Release();
        }
    }

    private (List<SnapshotEntryRecord> Entries, List<ContentBlobRecord> Blobs, List<SkippedFile> Skipped, long TotalBytes, long NewBytes)
        CaptureOverlay(GitRepository repository, RepositoryStatus status, bool hasUnmergedIndex, CancellationToken cancellationToken)
    {
        var entries = new List<SnapshotEntryRecord>();
        var blobs = new Dictionary<string, ContentBlobRecord>(StringComparer.Ordinal);
        var skipped = new List<SkippedFile>();
        long totalBytes = 0, newBytes = 0;
        var root = repository.Info.RootPath;
        var format = repository.Info.ObjectFormat;

        foreach (var entry in status.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IndexChange == ChangeKind.Ignored) continue;
            if (entry.IsSubmodule)
            {
                skipped.Add(new SkippedFile(entry.Path, "submodule"));
                continue;
            }

            SnapshotEntryKind kind;
            if (entry.IsConflicted) kind = SnapshotEntryKind.Conflicted;
            else if (entry.IsUntracked) kind = SnapshotEntryKind.Untracked;
            else if (entry.WorktreeChange == ChangeKind.Deleted) kind = SnapshotEntryKind.Deleted;
            else if (entry.WorktreeChange != ChangeKind.None) kind = SnapshotEntryKind.Modified;
            // Index ağacı HEAD'e düştüyse (unmerged) stage edilmiş değişiklikler de overlay'e alınır.
            else if (hasUnmergedIndex && entry.HasStagedChanges)
                kind = entry.IndexChange == ChangeKind.Deleted ? SnapshotEntryKind.Deleted : SnapshotEntryKind.Modified;
            else continue;

            if (hasUnmergedIndex && entry.OriginalPath is not null && entry.HasStagedChanges)
                entries.Add(new SnapshotEntryRecord(entry.OriginalPath, SnapshotEntryKind.Deleted, null, null, null));

            if (kind == SnapshotEntryKind.Deleted)
            {
                entries.Add(new SnapshotEntryRecord(entry.Path, kind, null, null, null));
                continue;
            }

            if (entries.Count >= Options.MaxOverlayFiles || totalBytes >= Options.MaxOverlayBytes)
            {
                skipped.Add(new SkippedFile(entry.Path, "snapshot size limit reached"));
                continue;
            }

            var fullPath = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            var (readStatus, content, error) = WorkingTreeFiles.Read(fullPath, Options.MaxFileSizeBytes, entry.WorktreeMode);
            switch (readStatus)
            {
                case WorkingTreeFiles.ReadStatus.Missing:
                    if (!entry.IsUntracked) entries.Add(new SnapshotEntryRecord(entry.Path, SnapshotEntryKind.Deleted, null, null, null));
                    continue;
                case WorkingTreeFiles.ReadStatus.TooLarge:
                case WorkingTreeFiles.ReadStatus.Unreadable:
                    skipped.Add(new SkippedFile(entry.Path, error ?? "unreadable"));
                    continue;
            }

            var stored = store.Put(content.Bytes, format, entry.Path);
            blobs[stored.BlobId] = new ContentBlobRecord(stored.BlobId, stored.Size, stored.StoredSize, stored.Compression, DateTimeOffset.Now);
            totalBytes += stored.Size;
            if (stored.IsNew) newBytes += stored.StoredSize;
            entries.Add(new SnapshotEntryRecord(entry.Path, kind, stored.BlobId, content.Mode, stored.Size));
        }

        var distinct = entries.GroupBy(e => e.Path, StringComparer.Ordinal).Select(g => g.Last()).OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
        return (distinct, blobs.Values.ToList(), skipped, totalBytes, newBytes);
    }

    internal static string ComputeFingerprint(string? headSha, string indexTree, IEnumerable<SnapshotEntryRecord> entries)
    {
        var builder = new StringBuilder();
        builder.Append(headSha).Append('\n').Append(indexTree).Append('\n');
        foreach (var entry in entries.OrderBy(e => e.Path, StringComparer.Ordinal))
            builder.Append(entry.Path).Append('\0').Append(entry.Kind).Append('\0').Append(entry.BlobId).Append('\0').Append(entry.Mode).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
