using ArChrono.Git;
using ArChrono.Recovery.Content;
using ArChrono.Storage.Records;
using ArChrono.Storage.Stores;

namespace ArChrono.Recovery.Snapshots;

public sealed record RestoreOptions
{
    /// <summary>Snapshot'tan sonra oluşturulmuş untracked dosyaları da sil (varsayılan: koru).</summary>
    public bool RemoveFilesCreatedAfterSnapshot { get; init; }

    /// <summary>Doluysa yalnızca bu dosyalar geri yüklenir; index'e dokunulmaz.</summary>
    public IReadOnlyCollection<string>? OnlyPaths { get; init; }
}

public sealed record RestoreReport(int WrittenFiles, int DeletedFiles, IReadOnlyList<string> Failures);

/// <summary>Snapshot'ı çalışma alanına ve index'e geri yükler (snapshot-engine.md §4).</summary>
public sealed class WorkingTreeRestorer(ArChronoStorage storage, ContentStore store)
{
    public async Task<RestoreReport> RestoreAsync(GitRepository repository, SnapshotRecord snapshot, RestoreOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new RestoreOptions();
        return options.OnlyPaths is { } paths
            ? await RestorePathsAsync(repository, snapshot, paths, cancellationToken).ConfigureAwait(false)
            : await RestoreAllAsync(repository, snapshot, options, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RestoreReport> RestoreAllAsync(GitRepository repository, SnapshotRecord snapshot, RestoreOptions options, CancellationToken cancellationToken)
    {
        var root = repository.Info.RootPath;
        var format = repository.Info.ObjectFormat;
        var failures = new List<string>();
        int written = 0, deleted = 0;

        var currentIndex = (await repository.Objects.ListIndexAsync(cancellationToken).ConfigureAwait(false))
            .Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        var snapshotIndex = (await repository.Objects.ListTreeAsync(snapshot.IndexTree, cancellationToken).ConfigureAwait(false))
            .ToDictionary(e => e.Path, StringComparer.Ordinal);
        var overlay = storage.Snapshots.GetEntries(snapshot.Id).ToDictionary(e => e.Path, StringComparer.Ordinal);

        await repository.Objects.ReadTreeIntoIndexAsync(snapshot.IndexTree, cancellationToken).ConfigureAwait(false);

        // Index artık snapshot'ınki; çalışma alanında farklı görünen takipli dosyaları index'ten yaz.
        var afterIndex = await repository.Status.GetStatusAsync(cancellationToken, isInternal: true).ConfigureAwait(false);
        var toCheckout = afterIndex.Entries
            .Where(e => !e.IsUntracked && !e.IsSubmodule && e.WorktreeChange != Git.Models.ChangeKind.None)
            .Select(e => e.Path)
            .Where(p => !overlay.ContainsKey(p) && snapshotIndex.TryGetValue(p, out var t) && t.Type == "blob")
            .ToList();
        foreach (var path in toCheckout)
        {
            // checkout-index mevcut klasör/dosya çakışmalarında başarısız olabilir; önce yolu temizle.
            var full = FullPath(root, path);
            if (Directory.Exists(full) && new FileInfo(full).LinkTarget is null) Directory.Delete(full, recursive: true);
        }
        try
        {
            await repository.Objects.CheckoutIndexAsync(toCheckout, cancellationToken).ConfigureAwait(false);
            written += toCheckout.Count;
        }
        catch (Git.Errors.GitException ex)
        {
            failures.Add("checkout-index: " + ex.Error.Explanation);
        }

        foreach (var entry in overlay.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = FullPath(root, entry.Path);
            try
            {
                if (entry.Kind == SnapshotEntryKind.Deleted || entry.BlobId is null)
                {
                    if (WorkingTreeFiles.Delete(full, root)) deleted++;
                    continue;
                }
                var content = store.Get(entry.BlobId, format);
                if (WorkingTreeFiles.Write(full, content, entry.Mode ?? WorkingTreeFiles.ModeRegular)) written++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                failures.Add($"{entry.Path}: {ex.Message}");
            }
        }

        // Geri yüklemeden önce takip edilen ama snapshot'ta olmayan dosyalar kaldırılır.
        // (Geri yüklemeden önce alınan recovery point bu dosyaları içerir.)
        foreach (var path in currentIndex)
        {
            if (snapshotIndex.ContainsKey(path) || overlay.ContainsKey(path)) continue;
            try
            {
                if (WorkingTreeFiles.Delete(FullPath(root, path), root)) deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{path}: {ex.Message}");
            }
        }

        if (options.RemoveFilesCreatedAfterSnapshot)
        {
            foreach (var entry in afterIndex.Entries.Where(e => e.IsUntracked))
            {
                if (overlay.ContainsKey(entry.Path) || snapshotIndex.ContainsKey(entry.Path) || currentIndex.Contains(entry.Path)) continue;
                try
                {
                    if (WorkingTreeFiles.Delete(FullPath(root, entry.Path), root)) deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{entry.Path}: {ex.Message}");
                }
            }
        }

        await repository.Objects.RefreshIndexAsync(cancellationToken).ConfigureAwait(false);
        return new RestoreReport(written, deleted, failures);
    }

    private async Task<RestoreReport> RestorePathsAsync(GitRepository repository, SnapshotRecord snapshot, IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        var root = repository.Info.RootPath;
        var failures = new List<string>();
        int written = 0, deleted = 0;

        foreach (var path in paths)
        {
            var full = FullPath(root, path);
            try
            {
                var entry = storage.Snapshots.GetEntry(snapshot.Id, path);
                byte[]? content;
                int mode;
                if (entry is not null)
                {
                    content = entry.Kind == SnapshotEntryKind.Deleted || entry.BlobId is null ? null : store.Get(entry.BlobId, repository.Info.ObjectFormat);
                    mode = entry.Mode ?? WorkingTreeFiles.ModeRegular;
                }
                else
                {
                    content = await repository.Objects.ReadBlobAsync($"{snapshot.IndexTree}:{path}", cancellationToken).ConfigureAwait(false);
                    var treeEntry = (await repository.Objects.ListTreeAsync(snapshot.IndexTree, cancellationToken).ConfigureAwait(false))
                        .FirstOrDefault(e => e.Path == path);
                    mode = treeEntry?.Mode ?? WorkingTreeFiles.ModeRegular;
                }

                if (content is null)
                {
                    if (WorkingTreeFiles.Delete(full, root)) deleted++;
                }
                else if (WorkingTreeFiles.Write(full, content, mode))
                {
                    written++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                failures.Add($"{path}: {ex.Message}");
            }
        }
        return new RestoreReport(written, deleted, failures);
    }

    private static string FullPath(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
}
