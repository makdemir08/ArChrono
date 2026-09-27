using ArChrono.Storage.Records;
using Microsoft.Data.Sqlite;

namespace ArChrono.Storage.Stores;

public sealed class SnapshotStore(ArChronoDatabase database)
{
    private const string Columns =
        "id, repository_id, created_at, trigger, label, head_ref, head_sha, index_tree, pin_commit, fingerprint, entry_count, total_bytes, new_bytes, skipped_json";

    /// <summary>Snapshot'ı, overlay girdilerini, yeni blob kayıtlarını ve pin kaydını tek transaction'da yazar.</summary>
    public SnapshotRecord Insert(SnapshotRecord snapshot, IReadOnlyCollection<SnapshotEntryRecord> entries,
        IReadOnlyCollection<ContentBlobRecord> newBlobs, string? pinCommonDir)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var blob in newBlobs)
        {
            using var command = connection.Command(
                "INSERT OR IGNORE INTO content_blob (blob_id, size, stored_size, compression, created_at) VALUES (@id, @size, @stored, @compression, @created)",
                transaction, ("@id", blob.BlobId), ("@size", blob.Size), ("@stored", blob.StoredSize), ("@compression", blob.Compression), ("@created", Db.ToMs(blob.CreatedAt)));
            command.ExecuteNonQuery();
        }

        if (snapshot.PinCommit is not null && pinCommonDir is not null)
        {
            using var pin = connection.Command("INSERT OR IGNORE INTO git_pin (common_dir, commit_sha, created_at) VALUES (@dir, @sha, @now)",
                transaction, ("@dir", pinCommonDir), ("@sha", snapshot.PinCommit), ("@now", Db.Now()));
            pin.ExecuteNonQuery();
        }

        var id = connection.InsertReturningId(
            """
            INSERT INTO snapshot (repository_id, created_at, trigger, label, head_ref, head_sha, index_tree, pin_commit, fingerprint, entry_count, total_bytes, new_bytes, skipped_json)
            VALUES (@repo, @created, @trigger, @label, @headRef, @headSha, @tree, @pin, @fingerprint, @count, @total, @new, @skipped)
            """, transaction,
            ("@repo", snapshot.RepositoryId), ("@created", Db.ToMs(snapshot.CreatedAt)), ("@trigger", snapshot.Trigger.ToDb()), ("@label", snapshot.Label),
            ("@headRef", snapshot.HeadRef), ("@headSha", snapshot.HeadSha), ("@tree", snapshot.IndexTree), ("@pin", snapshot.PinCommit),
            ("@fingerprint", snapshot.Fingerprint), ("@count", entries.Count), ("@total", snapshot.TotalBytes), ("@new", snapshot.NewBytes), ("@skipped", snapshot.SkippedJson));

        using (var insertEntry = connection.Command(
                   "INSERT INTO snapshot_entry (snapshot_id, path, kind, blob_id, mode, size) VALUES (@snapshot, @path, @kind, @blob, @mode, @size)", transaction))
        {
            var pPath = insertEntry.Parameters.Add("@path", SqliteType.Text);
            var pKind = insertEntry.Parameters.Add("@kind", SqliteType.Text);
            var pBlob = insertEntry.Parameters.Add("@blob", SqliteType.Text);
            var pMode = insertEntry.Parameters.Add("@mode", SqliteType.Integer);
            var pSize = insertEntry.Parameters.Add("@size", SqliteType.Integer);
            insertEntry.Parameters.AddWithValue("@snapshot", id);
            foreach (var entry in entries)
            {
                pPath.Value = entry.Path;
                pKind.Value = entry.Kind.ToDb();
                pBlob.Value = (object?)entry.BlobId ?? DBNull.Value;
                pMode.Value = (object?)entry.Mode ?? DBNull.Value;
                pSize.Value = (object?)entry.Size ?? DBNull.Value;
                insertEntry.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        return snapshot with { Id = id, EntryCount = entries.Count };
    }

    public SnapshotRecord? Get(long id)
    {
        using var connection = database.Open();
        using var command = connection.Command($"SELECT {Columns} FROM snapshot WHERE id = @id", null, ("@id", id));
        return command.ReadAll(Map).FirstOrDefault();
    }

    public IReadOnlyList<SnapshotEntryRecord> GetEntries(long snapshotId)
    {
        using var connection = database.Open();
        using var command = connection.Command("SELECT path, kind, blob_id, mode, size FROM snapshot_entry WHERE snapshot_id = @id ORDER BY path", null, ("@id", snapshotId));
        return command.ReadAll(MapEntry);
    }

    public SnapshotEntryRecord? GetEntry(long snapshotId, string path)
    {
        using var connection = database.Open();
        using var command = connection.Command("SELECT path, kind, blob_id, mode, size FROM snapshot_entry WHERE snapshot_id = @id AND path = @path",
            null, ("@id", snapshotId), ("@path", path));
        return command.ReadAll(MapEntry).FirstOrDefault();
    }

    public SnapshotRecord? GetLatest(long repositoryId)
    {
        using var connection = database.Open();
        using var command = connection.Command($"SELECT {Columns} FROM snapshot WHERE repository_id = @repo ORDER BY created_at DESC, id DESC LIMIT 1", null, ("@repo", repositoryId));
        return command.ReadAll(Map).FirstOrDefault();
    }

    public SnapshotRecord? FindAtOrBefore(long repositoryId, DateTimeOffset time)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            $"SELECT {Columns} FROM snapshot WHERE repository_id = @repo AND created_at <= @time ORDER BY created_at DESC, id DESC LIMIT 1",
            null, ("@repo", repositoryId), ("@time", Db.ToMs(time)));
        return command.ReadAll(Map).FirstOrDefault();
    }

    public IReadOnlyList<SnapshotRecord> List(long repositoryId, DateTimeOffset? from = null, DateTimeOffset? to = null, int limit = 500)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            $"""
             SELECT {Columns} FROM snapshot
             WHERE repository_id = @repo AND created_at >= @from AND created_at <= @to
             ORDER BY created_at DESC, id DESC LIMIT @limit
             """,
            null, ("@repo", repositoryId), ("@from", from is null ? 0 : Db.ToMs(from.Value)), ("@to", to is null ? long.MaxValue : Db.ToMs(to.Value)), ("@limit", limit));
        return command.ReadAll(Map);
    }

    /// <summary>Aynı (HEAD, index ağacı) için daha önce oluşturulmuş pin commit'i.</summary>
    public string? FindPin(long repositoryId, string? headSha, string indexTree)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            "SELECT pin_commit FROM snapshot WHERE repository_id = @repo AND index_tree = @tree AND head_sha IS @head AND pin_commit IS NOT NULL ORDER BY id DESC LIMIT 1",
            null, ("@repo", repositoryId), ("@tree", indexTree), ("@head", headSha));
        return command.ExecuteScalar() as string;
    }

    /// <summary>Bir dosyanın overlay'de yer aldığı snapshot'lar (dosya zaman çizelgesi için).</summary>
    public IReadOnlyList<(long SnapshotId, DateTimeOffset CreatedAt, SnapshotEntryRecord Entry)> GetPathEntries(long repositoryId, string path, int limit = 500)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            """
            SELECT s.id, s.created_at, e.path, e.kind, e.blob_id, e.mode, e.size
            FROM snapshot_entry e JOIN snapshot s ON s.id = e.snapshot_id
            WHERE s.repository_id = @repo AND e.path = @path
            ORDER BY s.created_at DESC LIMIT @limit
            """, null, ("@repo", repositoryId), ("@path", path), ("@limit", limit));
        return command.ReadAll(r => (r.GetInt64(0), Db.FromMs(r.GetInt64(1)),
            new SnapshotEntryRecord(r.GetString(2), EnumText.Parse<SnapshotEntryKind>(r.GetString(3)), r.StringOrNull(4), r.Int32OrNull(5), r.Int64OrNull(6))));
    }

    public void Delete(IReadOnlyCollection<long> snapshotIds)
    {
        if (snapshotIds.Count == 0) return;
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.Command("DELETE FROM snapshot WHERE id = @id", transaction);
        var parameter = command.Parameters.Add("@id", SqliteType.Integer);
        foreach (var id in snapshotIds)
        {
            parameter.Value = id;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>Hiçbir recovery point'in kullanmadığı, verilen zamandan eski snapshot'lar (en eski önce).</summary>
    public IReadOnlyList<long> GetUnreferencedOlderThan(DateTimeOffset cutoff, int limit)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            """
            SELECT s.id FROM snapshot s
            WHERE s.created_at < @cutoff
              AND NOT EXISTS (SELECT 1 FROM recovery_point p WHERE p.snapshot_id = s.id)
            ORDER BY s.created_at ASC LIMIT @limit
            """, null, ("@cutoff", Db.ToMs(cutoff)), ("@limit", limit));
        return command.ReadAll(r => r.GetInt64(0));
    }

    /// <summary>Bir recovery point tarafından referans verilen snapshot id'leri.</summary>
    public HashSet<long> GetSnapshotsReferencedByPoints(long repositoryId)
    {
        using var connection = database.Open();
        using var command = connection.Command("SELECT snapshot_id FROM recovery_point WHERE repository_id = @repo AND snapshot_id IS NOT NULL", null, ("@repo", repositoryId));
        return command.ReadAll(r => r.GetInt64(0)).ToHashSet();
    }

    private static SnapshotRecord Map(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), Db.FromMs(r.GetInt64(2)), EnumText.Parse<SnapshotTrigger>(r.GetString(3)), r.StringOrNull(4),
        r.StringOrNull(5), r.StringOrNull(6), r.GetString(7), r.StringOrNull(8), r.GetString(9), r.GetInt32(10), r.GetInt64(11), r.GetInt64(12), r.StringOrNull(13));

    private static SnapshotEntryRecord MapEntry(SqliteDataReader r) => new(
        r.GetString(0), EnumText.Parse<SnapshotEntryKind>(r.GetString(1)), r.StringOrNull(2), r.Int32OrNull(3), r.Int64OrNull(4));
}

public sealed class ContentBlobIndex(ArChronoDatabase database)
{
    public bool Exists(string blobId)
    {
        using var connection = database.Open();
        using var command = connection.Command("SELECT 1 FROM content_blob WHERE blob_id = @id", null, ("@id", blobId));
        return command.ExecuteScalar() is not null;
    }

    public long GetTotalStoredBytes()
    {
        using var connection = database.Open();
        using var command = connection.Command("SELECT COALESCE(SUM(stored_size), 0) FROM content_blob");
        return (long)command.ExecuteScalar()!;
    }

    public int Count()
    {
        using var connection = database.Open();
        using var command = connection.Command("SELECT COUNT(*) FROM content_blob");
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public IReadOnlyList<ContentBlobRecord> GetUnreferenced(int limit = 10_000)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            """
            SELECT b.blob_id, b.size, b.stored_size, b.compression, b.created_at FROM content_blob b
            WHERE NOT EXISTS (SELECT 1 FROM snapshot_entry e WHERE e.blob_id = b.blob_id)
            LIMIT @limit
            """, null, ("@limit", limit));
        return command.ReadAll(r => new ContentBlobRecord(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), Db.FromMs(r.GetInt64(4))));
    }

    public HashSet<string> GetAllIds()
    {
        using var connection = database.Open();
        using var command = connection.Command("SELECT blob_id FROM content_blob");
        return command.ReadAll(r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
    }

    public void Delete(IReadOnlyCollection<string> blobIds)
    {
        if (blobIds.Count == 0) return;
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.Command("DELETE FROM content_blob WHERE blob_id = @id", transaction);
        var parameter = command.Parameters.Add("@id", SqliteType.Text);
        foreach (var id in blobIds)
        {
            parameter.Value = id;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
