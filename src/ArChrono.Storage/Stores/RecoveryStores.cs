using ArChrono.Storage.Records;
using Microsoft.Data.Sqlite;

namespace ArChrono.Storage.Stores;

public sealed class RecoveryPointStore(ArChronoDatabase database)
{
    private const string Columns =
        "id, repository_id, created_at, kind, title, operation_id, head_ref, head_sha, branch, repo_state, snapshot_id, changed_file_count, is_pinned, metadata_json";

    public RecoveryPointRecord Insert(RecoveryPointRecord point, IReadOnlyCollection<RecoveryRefRecord> refs, IReadOnlyCollection<RecoveryStashRecord> stashes)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        var id = connection.InsertReturningId(
            """
            INSERT INTO recovery_point (repository_id, created_at, kind, title, operation_id, head_ref, head_sha, branch, repo_state, snapshot_id, changed_file_count, is_pinned, metadata_json)
            VALUES (@repo, @created, @kind, @title, @op, @headRef, @headSha, @branch, @state, @snapshot, @changed, @pinned, @meta)
            """, transaction,
            ("@repo", point.RepositoryId), ("@created", Db.ToMs(point.CreatedAt)), ("@kind", point.Kind.ToDb()), ("@title", point.Title),
            ("@op", point.OperationId), ("@headRef", point.HeadRef), ("@headSha", point.HeadSha), ("@branch", point.Branch),
            ("@state", point.RepoState), ("@snapshot", point.SnapshotId), ("@changed", point.ChangedFileCount), ("@pinned", point.IsPinned ? 1 : 0),
            ("@meta", point.MetadataJson));

        using (var insertRef = connection.Command("INSERT INTO recovery_ref (recovery_point_id, ref_name, target_sha) VALUES (@point, @name, @sha)", transaction))
        {
            insertRef.Parameters.AddWithValue("@point", id);
            var name = insertRef.Parameters.Add("@name", SqliteType.Text);
            var sha = insertRef.Parameters.Add("@sha", SqliteType.Text);
            foreach (var r in refs)
            {
                name.Value = r.RefName;
                sha.Value = r.TargetSha;
                insertRef.ExecuteNonQuery();
            }
        }

        using (var insertStash = connection.Command("INSERT INTO recovery_stash (recovery_point_id, position, commit_sha, message) VALUES (@point, @pos, @sha, @msg)", transaction))
        {
            insertStash.Parameters.AddWithValue("@point", id);
            var pos = insertStash.Parameters.Add("@pos", SqliteType.Integer);
            var sha = insertStash.Parameters.Add("@sha", SqliteType.Text);
            var msg = insertStash.Parameters.Add("@msg", SqliteType.Text);
            foreach (var s in stashes)
            {
                pos.Value = s.Position;
                sha.Value = s.CommitSha;
                msg.Value = s.Message;
                insertStash.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        return point with { Id = id };
    }

    public RecoveryPointRecord? Get(long id)
    {
        using var connection = database.Open();
        using var command = connection.Command($"SELECT {Columns} FROM recovery_point WHERE id = @id", null, ("@id", id));
        return command.ReadAll(Map).FirstOrDefault();
    }

    public IReadOnlyList<RecoveryRefRecord> GetRefs(long pointId)
    {
        using var connection = database.Open();
        using var command = connection.Command("SELECT ref_name, target_sha FROM recovery_ref WHERE recovery_point_id = @id ORDER BY ref_name", null, ("@id", pointId));
        return command.ReadAll(r => new RecoveryRefRecord(r.GetString(0), r.GetString(1)));
    }

    public IReadOnlyList<RecoveryStashRecord> GetStashes(long pointId)
    {
        using var connection = database.Open();
        using var command = connection.Command("SELECT position, commit_sha, message FROM recovery_stash WHERE recovery_point_id = @id ORDER BY position", null, ("@id", pointId));
        return command.ReadAll(r => new RecoveryStashRecord(r.GetInt32(0), r.GetString(1), r.GetString(2)));
    }

    public IReadOnlyList<RecoveryPointRecord> List(long repositoryId, int limit = 500, DateTimeOffset? before = null)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            $"SELECT {Columns} FROM recovery_point WHERE repository_id = @repo AND created_at <= @before ORDER BY created_at DESC, id DESC LIMIT @limit",
            null, ("@repo", repositoryId), ("@before", before is null ? long.MaxValue : Db.ToMs(before.Value)), ("@limit", limit));
        return command.ReadAll(Map);
    }

    /// <summary>Sabitlenmemiş, verilen zamandan eski recovery point'ler (en eski önce).</summary>
    public IReadOnlyList<long> GetExpired(DateTimeOffset cutoff, int limit)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            "SELECT id FROM recovery_point WHERE created_at < @cutoff AND is_pinned = 0 ORDER BY created_at ASC LIMIT @limit",
            null, ("@cutoff", Db.ToMs(cutoff)), ("@limit", limit));
        return command.ReadAll(r => r.GetInt64(0));
    }

    public void SetPinned(long id, bool pinned)
    {
        using var connection = database.Open();
        using var command = connection.Command("UPDATE recovery_point SET is_pinned = @v WHERE id = @id", null, ("@id", id), ("@v", pinned ? 1 : 0));
        command.ExecuteNonQuery();
    }

    public void Delete(IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0) return;
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.Command("DELETE FROM recovery_point WHERE id = @id", transaction);
        var parameter = command.Parameters.Add("@id", SqliteType.Integer);
        foreach (var id in ids)
        {
            parameter.Value = id;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static RecoveryPointRecord Map(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), Db.FromMs(r.GetInt64(2)), EnumText.Parse<RecoveryPointKind>(r.GetString(3)), r.GetString(4),
        r.Int64OrNull(5), r.StringOrNull(6), r.StringOrNull(7), r.StringOrNull(8), r.GetString(9), r.Int64OrNull(10), r.GetInt32(11),
        r.GetInt64(12) != 0, r.StringOrNull(13));
}

public sealed class OperationStore(ArChronoDatabase database)
{
    private const string Columns =
        "id, repository_id, kind, title, command_text, risk, status, started_at, finished_at, error_code, error_message, error_details, before_point_id, after_point_id, undo_of_operation_id, undone_by_operation_id, metadata_json";

    public GitOperationRecord Start(long repositoryId, string kind, string title, string commandText, OperationRisk risk, long? undoOfOperationId = null, string? metadataJson = null)
    {
        using var connection = database.Open();
        var now = Db.Now();
        var id = connection.InsertReturningId(
            """
            INSERT INTO git_operation (repository_id, kind, title, command_text, risk, status, started_at, undo_of_operation_id, metadata_json)
            VALUES (@repo, @kind, @title, @cmd, @risk, 'running', @now, @undoOf, @meta)
            """, null,
            ("@repo", repositoryId), ("@kind", kind), ("@title", title), ("@cmd", commandText), ("@risk", risk.ToDb()), ("@now", now),
            ("@undoOf", undoOfOperationId), ("@meta", metadataJson));
        return new GitOperationRecord(id, repositoryId, kind, title, commandText, risk, OperationStatus.Running, Db.FromMs(now), null,
            null, null, null, null, null, undoOfOperationId, null, metadataJson);
    }

    public void SetBeforePoint(long operationId, long pointId)
    {
        using var connection = database.Open();
        using var command = connection.Command("UPDATE git_operation SET before_point_id = @p WHERE id = @id", null, ("@id", operationId), ("@p", pointId));
        command.ExecuteNonQuery();
    }

    public void Finish(long operationId, OperationStatus status, long? afterPointId, string? errorCode = null, string? errorMessage = null, string? errorDetails = null, string? metadataJson = null)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.Command(
                   """
                   UPDATE git_operation SET status = @status, finished_at = @now, after_point_id = @after,
                       error_code = @code, error_message = @message, error_details = @details,
                       metadata_json = COALESCE(@meta, metadata_json)
                   WHERE id = @id
                   """, transaction,
                   ("@id", operationId), ("@status", status.ToDb()), ("@now", Db.Now()), ("@after", afterPointId),
                   ("@code", errorCode), ("@message", errorMessage), ("@details", errorDetails), ("@meta", metadataJson)))
        {
            command.ExecuteNonQuery();
        }

        // Undo/redo başarıyla bittiyse hedef işlemi "geri alındı" olarak işaretle.
        // Redo (bir undo'nun geri alınması) orijinal işlemi yeniden geri alınabilir yapar.
        if (status == OperationStatus.Succeeded)
        {
            using var mark = connection.Command(
                """
                UPDATE git_operation SET undone_by_operation_id = @id
                WHERE id = (SELECT undo_of_operation_id FROM git_operation WHERE id = @id);

                UPDATE git_operation SET undone_by_operation_id = NULL
                WHERE (SELECT kind FROM git_operation WHERE id = @id) = 'redo'
                  AND id = (SELECT u.undo_of_operation_id FROM git_operation u
                            WHERE u.id = (SELECT undo_of_operation_id FROM git_operation WHERE id = @id));
                """, transaction, ("@id", operationId));
            mark.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public GitOperationRecord? Get(long id)
    {
        using var connection = database.Open();
        using var command = connection.Command($"SELECT {Columns} FROM git_operation WHERE id = @id", null, ("@id", id));
        return command.ReadAll(Map).FirstOrDefault();
    }

    public IReadOnlyList<GitOperationRecord> List(long repositoryId, int limit = 200)
    {
        using var connection = database.Open();
        using var command = connection.Command($"SELECT {Columns} FROM git_operation WHERE repository_id = @repo ORDER BY started_at DESC, id DESC LIMIT @limit",
            null, ("@repo", repositoryId), ("@limit", limit));
        return command.ReadAll(Map);
    }

    /// <summary>Geri alınabilecek en son işlem: before noktası olan, henüz geri alınmamış, undo/redo olmayan işlem.</summary>
    public GitOperationRecord? GetLastUndoable(long repositoryId)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            $"""
             SELECT {Columns} FROM git_operation
             WHERE repository_id = @repo AND before_point_id IS NOT NULL AND undone_by_operation_id IS NULL
               AND kind NOT IN ('undo', 'redo') AND status IN ('succeeded', 'conflicted', 'failed')
             ORDER BY id DESC LIMIT 1
             """, null, ("@repo", repositoryId));
        return command.ReadAll(Map).FirstOrDefault();
    }

    /// <summary>Yeniden uygulanabilecek en son undo: ondan sonra yeni bir işlem yapılmamış olmalı.</summary>
    public GitOperationRecord? GetLastRedoable(long repositoryId)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            $"""
             SELECT {Columns} FROM git_operation u
             WHERE u.repository_id = @repo AND u.kind = 'undo' AND u.status = 'succeeded'
               AND u.undone_by_operation_id IS NULL AND u.before_point_id IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM git_operation o
                               WHERE o.repository_id = @repo AND o.id > u.id AND o.kind NOT IN ('undo', 'redo')
                                 AND o.status <> 'blocked')
             ORDER BY u.id DESC LIMIT 1
             """, null, ("@repo", repositoryId));
        return command.ReadAll(Map).FirstOrDefault();
    }

    private static GitOperationRecord Map(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), EnumText.Parse<OperationRisk>(r.GetString(5)),
        EnumText.Parse<OperationStatus>(r.GetString(6)), Db.FromMs(r.GetInt64(7)), r.IsDBNull(8) ? null : Db.FromMs(r.GetInt64(8)),
        r.StringOrNull(9), r.StringOrNull(10), r.StringOrNull(11), r.Int64OrNull(12), r.Int64OrNull(13), r.Int64OrNull(14), r.Int64OrNull(15), r.StringOrNull(16));
}

public sealed class PinStore(ArChronoDatabase database)
{
    public void Add(string commonDir, string commitSha)
    {
        using var connection = database.Open();
        using var command = connection.Command("INSERT OR IGNORE INTO git_pin (common_dir, commit_sha, created_at) VALUES (@dir, @sha, @now)",
            null, ("@dir", commonDir), ("@sha", commitSha), ("@now", Db.Now()));
        command.ExecuteNonQuery();
    }

    public bool Exists(string commonDir, string commitSha)
    {
        using var connection = database.Open();
        using var command = connection.Command("SELECT 1 FROM git_pin WHERE common_dir = @dir AND commit_sha = @sha", null, ("@dir", commonDir), ("@sha", commitSha));
        return command.ExecuteScalar() is not null;
    }

    public IReadOnlyList<string> List(string commonDir)
    {
        using var connection = database.Open();
        using var command = connection.Command("SELECT commit_sha FROM git_pin WHERE common_dir = @dir", null, ("@dir", commonDir));
        return command.ReadAll(r => r.GetString(0));
    }

    /// <summary>Aynı common_dir'e bağlı hiçbir snapshot veya recovery ref tarafından kullanılmayan pin'ler.</summary>
    public IReadOnlyList<string> GetUnreferenced(string commonDir)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            """
            SELECT p.commit_sha FROM git_pin p
            WHERE p.common_dir = @dir
              AND NOT EXISTS (SELECT 1 FROM snapshot s JOIN repository r ON r.id = s.repository_id
                              WHERE r.common_dir = @dir AND s.pin_commit = p.commit_sha)
              AND NOT EXISTS (SELECT 1 FROM recovery_ref rr JOIN recovery_point rp ON rp.id = rr.recovery_point_id
                              JOIN repository r ON r.id = rp.repository_id
                              WHERE r.common_dir = @dir AND rr.target_sha = p.commit_sha)
              AND NOT EXISTS (SELECT 1 FROM recovery_stash rs JOIN recovery_point rp ON rp.id = rs.recovery_point_id
                              JOIN repository r ON r.id = rp.repository_id
                              WHERE r.common_dir = @dir AND rs.commit_sha = p.commit_sha)
              AND NOT EXISTS (SELECT 1 FROM recovery_point rp JOIN repository r ON r.id = rp.repository_id
                              WHERE r.common_dir = @dir AND rp.head_sha = p.commit_sha)
            """, null, ("@dir", commonDir));
        return command.ReadAll(r => r.GetString(0));
    }

    public void Remove(string commonDir, IReadOnlyCollection<string> shas)
    {
        if (shas.Count == 0) return;
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.Command("DELETE FROM git_pin WHERE common_dir = @dir AND commit_sha = @sha", transaction);
        command.Parameters.AddWithValue("@dir", commonDir);
        var sha = command.Parameters.Add("@sha", SqliteType.Text);
        foreach (var s in shas)
        {
            sha.Value = s;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
