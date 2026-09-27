using ArChrono.Git;
using ArChrono.Git.Diffing;
using ArChrono.Git.Models;
using ArChrono.Recovery.Content;
using ArChrono.Storage.Records;
using ArChrono.Storage.Stores;

namespace ArChrono.Recovery.Snapshots;

public enum FileSource
{
    GitObject,
    ContentStore,
    WorkingTree,
}

public sealed record SnapshotFile(string Path, string BlobId, int Mode, FileSource Source);

public sealed record SnapshotFileChange(string Path, ChangeKind Change, SnapshotFile? Old, SnapshotFile? New);

/// <summary>Snapshot içeriğini okur: dosyanın o anki hâli, etkin dosya haritası, iki durum arasındaki fark.</summary>
public sealed class SnapshotReader(ArChronoStorage storage, ContentStore store, SnapshotEngine engine)
{
    /// <summary>Dosyanın snapshot anındaki içeriği; dosya o anda yoksa null.</summary>
    public async Task<byte[]?> ReadFileAsync(GitRepository repository, SnapshotRecord snapshot, string path, CancellationToken cancellationToken = default)
    {
        var entry = storage.Snapshots.GetEntry(snapshot.Id, path);
        if (entry is not null)
        {
            if (entry.Kind == SnapshotEntryKind.Deleted || entry.BlobId is null) return null;
            return await ReadBlobAsync(repository, entry.BlobId, cancellationToken).ConfigureAwait(false);
        }
        return await repository.Objects.ReadBlobAsync($"{snapshot.IndexTree}:{path}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]?> ReadFileAsync(GitRepository repository, SnapshotFile file, CancellationToken cancellationToken = default) =>
        file.Source switch
        {
            FileSource.WorkingTree => ReadWorkingTreeFile(repository, file.Path),
            _ => await ReadBlobAsync(repository, file.BlobId, cancellationToken).ConfigureAwait(false),
        };

    public async Task<byte[]?> ReadBlobAsync(GitRepository repository, string blobId, CancellationToken cancellationToken = default)
    {
        if (store.TryGet(blobId, repository.Info.ObjectFormat, out var content)) return content;
        return await repository.Objects.ReadBlobAsync(blobId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Snapshot'taki tüm dosyalar: index ağacı + overlay.</summary>
    public async Task<IReadOnlyDictionary<string, SnapshotFile>> GetFileMapAsync(GitRepository repository, SnapshotRecord snapshot, CancellationToken cancellationToken = default)
    {
        var tree = await repository.Objects.ListTreeAsync(snapshot.IndexTree, cancellationToken).ConfigureAwait(false);
        var map = new Dictionary<string, SnapshotFile>(StringComparer.Ordinal);
        foreach (var entry in tree.Where(e => e.Type == "blob"))
            map[entry.Path] = new SnapshotFile(entry.Path, entry.ObjectId, entry.Mode, FileSource.GitObject);

        foreach (var entry in storage.Snapshots.GetEntries(snapshot.Id))
        {
            if (entry.Kind == SnapshotEntryKind.Deleted || entry.BlobId is null) map.Remove(entry.Path);
            else map[entry.Path] = new SnapshotFile(entry.Path, entry.BlobId, entry.Mode ?? WorkingTreeFiles.ModeRegular, FileSource.ContentStore);
        }
        return map;
    }

    /// <summary>Şu anki çalışma alanının dosya haritası (store'a yazmadan).</summary>
    public async Task<IReadOnlyDictionary<string, SnapshotFile>> GetCurrentFileMapAsync(GitRepository repository, CancellationToken cancellationToken = default)
    {
        var index = await repository.Objects.ListIndexAsync(cancellationToken).ConfigureAwait(false);
        var status = await repository.Status.GetStatusAsync(cancellationToken, isInternal: true).ConfigureAwait(false);
        var map = new Dictionary<string, SnapshotFile>(StringComparer.Ordinal);
        foreach (var entry in index.Where(e => e.Stage is 0 or 2 && e.Mode != 0xE000))
            map[entry.Path] = new SnapshotFile(entry.Path, entry.ObjectId, entry.Mode, FileSource.GitObject);

        foreach (var entry in status.Entries.Where(e => e.HasWorktreeChanges && !e.IsSubmodule))
        {
            var fullPath = Path.Combine(repository.Info.RootPath, entry.Path);
            var (readStatus, content, _) = WorkingTreeFiles.Read(fullPath, engine.Options.MaxFileSizeBytes, entry.WorktreeMode);
            if (readStatus == WorkingTreeFiles.ReadStatus.Missing)
            {
                map.Remove(entry.Path);
                continue;
            }
            if (readStatus != WorkingTreeFiles.ReadStatus.Ok) continue;
            map[entry.Path] = new SnapshotFile(entry.Path, ContentStore.ComputeBlobId(content.Bytes, repository.Info.ObjectFormat), content.Mode, FileSource.WorkingTree);
        }
        return map;
    }

    /// <summary><paramref name="newer"/> null ise şu anki çalışma alanı ile karşılaştırır.</summary>
    public async Task<IReadOnlyList<SnapshotFileChange>> CompareAsync(GitRepository repository, SnapshotRecord older, SnapshotRecord? newer, CancellationToken cancellationToken = default)
    {
        var oldMap = await GetFileMapAsync(repository, older, cancellationToken).ConfigureAwait(false);
        var newMap = newer is null
            ? await GetCurrentFileMapAsync(repository, cancellationToken).ConfigureAwait(false)
            : await GetFileMapAsync(repository, newer, cancellationToken).ConfigureAwait(false);
        return Compare(oldMap, newMap);
    }

    internal static IReadOnlyList<SnapshotFileChange> Compare(IReadOnlyDictionary<string, SnapshotFile> oldMap, IReadOnlyDictionary<string, SnapshotFile> newMap)
    {
        var changes = new List<SnapshotFileChange>();
        foreach (var (path, oldFile) in oldMap)
        {
            if (!newMap.TryGetValue(path, out var newFile)) changes.Add(new SnapshotFileChange(path, ChangeKind.Deleted, oldFile, null));
            else if (newFile.BlobId != oldFile.BlobId) changes.Add(new SnapshotFileChange(path, ChangeKind.Modified, oldFile, newFile));
            else if (newFile.Mode != oldFile.Mode) changes.Add(new SnapshotFileChange(path, ChangeKind.TypeChanged, oldFile, newFile));
        }
        foreach (var (path, newFile) in newMap)
        {
            if (!oldMap.ContainsKey(path)) changes.Add(new SnapshotFileChange(path, ChangeKind.Added, null, newFile));
        }
        return changes.OrderBy(c => c.Path, StringComparer.Ordinal).ToList();
    }

    public async Task<FileDiff> DiffAsync(GitRepository repository, SnapshotFileChange change, CancellationToken cancellationToken = default)
    {
        var oldContent = change.Old is null ? null : await ReadFileAsync(repository, change.Old, cancellationToken).ConfigureAwait(false);
        var newContent = change.New is null ? null : await ReadFileAsync(repository, change.New, cancellationToken).ConfigureAwait(false);
        return TextDiff.Compare(change.Path, change.Path, oldContent, newContent);
    }

    /// <summary>Dosyanın gerçekten değiştiği snapshot anları (yeniden eskiye).</summary>
    public async Task<IReadOnlyList<(SnapshotRecord Snapshot, string? BlobId)>> GetFileTimelineAsync(GitRepository repository, long repositoryId, string path,
        int maxSnapshots = 300, CancellationToken cancellationToken = default)
    {
        var snapshots = storage.Snapshots.List(repositoryId, limit: maxSnapshots);
        var result = new List<(SnapshotRecord, string?)>();
        string? previous = "\0";
        var treeCache = new Dictionary<string, string?>(StringComparer.Ordinal);

        // Eskiden yeniye gez, değişim anlarını topla.
        foreach (var snapshot in snapshots.Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? blob;
            var entry = storage.Snapshots.GetEntry(snapshot.Id, path);
            if (entry is not null)
            {
                blob = entry.Kind == SnapshotEntryKind.Deleted ? null : entry.BlobId;
            }
            else if (!treeCache.TryGetValue(snapshot.IndexTree, out blob))
            {
                blob = await repository.Refs.ResolveObjectAsync($"{snapshot.IndexTree}:{path}", cancellationToken).ConfigureAwait(false);
                treeCache[snapshot.IndexTree] = blob;
            }

            if (blob != previous) result.Add((snapshot, blob));
            previous = blob;
        }
        result.Reverse();
        return result;
    }

    private static byte[]? ReadWorkingTreeFile(GitRepository repository, string path)
    {
        var (status, content, _) = WorkingTreeFiles.Read(Path.Combine(repository.Info.RootPath, path), long.MaxValue, WorkingTreeFiles.ModeRegular);
        return status == WorkingTreeFiles.ReadStatus.Ok ? content.Bytes : null;
    }
}
