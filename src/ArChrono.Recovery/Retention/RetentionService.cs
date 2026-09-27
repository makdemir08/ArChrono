using ArChrono.Git;
using ArChrono.Recovery.Content;
using ArChrono.Recovery.Snapshots;
using ArChrono.Storage.Records;
using ArChrono.Storage.Stores;

namespace ArChrono.Recovery.Retention;

public sealed record RetentionPolicy
{
    /// <summary>null: "Until disk limit" — yalnızca depolama sınırı uygulanır.</summary>
    public TimeSpan? MaxAge { get; init; } = TimeSpan.FromDays(30);

    public long MaxStorageBytes { get; init; } = 5L * 1024 * 1024 * 1024;

    /// <summary>Bu süre içindeki işlem öncesi noktalar depolama baskısında bile silinmez.</summary>
    public TimeSpan ProtectRecent { get; init; } = TimeSpan.FromHours(24);
}

public sealed record RetentionReport(int DeletedSnapshots, int DeletedRecoveryPoints, int DeletedBlobs, long FreedBytes, int RemovedPins, long UsedBytesAfter);

/// <summary>Saklama politikası ve depolama sınırı. Kullanıcının Git nesne veritabanına dokunmaz; asla git gc çalıştırmaz.</summary>
public sealed class RetentionService(ArChronoStorage storage, ContentStore store, PinManager pins)
{
    public async Task<RetentionReport> RunAsync(RetentionPolicy policy, Func<RepositoryRecord, Task<GitRepository?>>? openRepository = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.Now;
        int deletedSnapshots = 0, deletedPoints = 0;

        // 1. Yaş sınırı
        if (policy.MaxAge is { } maxAge)
        {
            var cutoff = now - maxAge;
            var points = storage.RecoveryPoints.GetExpired(cutoff, limit: 100_000);
            storage.RecoveryPoints.Delete(points);
            deletedPoints += points.Count;

            var snapshots = storage.Snapshots.GetUnreferencedOlderThan(cutoff, limit: 100_000);
            storage.Snapshots.Delete(snapshots);
            deletedSnapshots += snapshots.Count;
        }

        var (blobs, freed) = await SweepAsync(cancellationToken).ConfigureAwait(false);
        blobs += await SweepOrphanFilesAsync(TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false);

        // 2. Depolama sınırı: önce eski zamanlayıcı snapshot'ları, sonra korunmayan eski recovery point'ler.
        var protectBefore = now - policy.ProtectRecent;
        for (var round = 0; round < 1000 && storage.Blobs.GetTotalStoredBytes() > policy.MaxStorageBytes; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshots = storage.Snapshots.GetUnreferencedOlderThan(now, limit: 50);
            if (snapshots.Count > 0)
            {
                storage.Snapshots.Delete(snapshots);
                deletedSnapshots += snapshots.Count;
            }
            else
            {
                var points = storage.RecoveryPoints.GetExpired(protectBefore, limit: 50);
                if (points.Count == 0) break;
                storage.RecoveryPoints.Delete(points);
                deletedPoints += points.Count;
            }
            var (b, f) = await SweepAsync(cancellationToken).ConfigureAwait(false);
            blobs += b;
            freed += f;
        }

        // 3. Referansı kalmayan pin ref'leri
        var removedPins = 0;
        if (openRepository is not null)
        {
            foreach (var group in storage.Repositories.GetRecent(int.MaxValue).GroupBy(r => r.CommonDir))
            {
                var repository = await openRepository(group.First()).ConfigureAwait(false);
                if (repository is null) continue;
                try
                {
                    removedPins += await pins.RemoveUnreferencedAsync(repository, cancellationToken).ConfigureAwait(false);
                }
                catch (Git.Errors.GitException)
                {
                }
            }
        }

        return new RetentionReport(deletedSnapshots, deletedPoints, blobs, freed, removedPins, storage.Blobs.GetTotalStoredBytes());
    }

    /// <summary>
    /// Veritabanında kaydı olmayan store dosyalarını siler (snapshot yazımı yarıda kesildiyse).
    /// Yeni yazılmış dosyalara, eşzamanlı bir snapshot kaydedilmeden dokunulmaması için yaş sınırı uygulanır.
    /// </summary>
    public async Task<int> SweepOrphanFilesAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
    {
        await store.MutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            store.CleanTemporaryFiles(olderThan);
            var known = storage.Blobs.GetAllIds();
            var removed = 0;
            foreach (var blobId in store.EnumerateBlobIds().ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (known.Contains(blobId) || DateTime.UtcNow - store.GetWriteTimeUtc(blobId) < olderThan) continue;
                store.Delete(blobId);
                removed++;
            }
            return removed;
        }
        finally
        {
            store.MutationLock.Release();
        }
    }

    /// <summary>Hiçbir snapshot'ın kullanmadığı blob'ları diskten ve veritabanından siler.</summary>
    public async Task<(int Count, long Bytes)> SweepAsync(CancellationToken cancellationToken = default)
    {
        await store.MutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int count = 0;
            long bytes = 0;
            while (true)
            {
                var unreferenced = storage.Blobs.GetUnreferenced(limit: 1000);
                if (unreferenced.Count == 0) break;
                foreach (var blob in unreferenced)
                {
                    store.Delete(blob.BlobId);
                    bytes += blob.StoredSize;
                }
                storage.Blobs.Delete(unreferenced.Select(b => b.BlobId).ToList());
                count += unreferenced.Count;
            }
            return (count, bytes);
        }
        finally
        {
            store.MutationLock.Release();
        }
    }
}
