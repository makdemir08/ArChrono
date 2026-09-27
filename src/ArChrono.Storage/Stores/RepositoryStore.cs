using ArChrono.Storage.Records;
using Microsoft.Data.Sqlite;

namespace ArChrono.Storage.Stores;

public sealed class RepositoryStore(ArChronoDatabase database)
{
    private const string Columns = "id, root_path, git_dir, common_dir, name, object_format, created_at, last_opened_at, is_favorite, time_machine_enabled";

    public RepositoryRecord Upsert(string rootPath, string gitDir, string commonDir, string name, string objectFormat)
    {
        using var connection = database.Open();
        using (var command = connection.Command(
                   """
                   INSERT INTO repository (root_path, git_dir, common_dir, name, object_format, created_at, last_opened_at)
                   VALUES (@root, @git, @common, @name, @format, @now, @now)
                   ON CONFLICT (root_path) DO UPDATE SET
                       git_dir = excluded.git_dir, common_dir = excluded.common_dir,
                       object_format = excluded.object_format, last_opened_at = excluded.last_opened_at
                   """, null,
                   ("@root", rootPath), ("@git", gitDir), ("@common", commonDir), ("@name", name), ("@format", objectFormat), ("@now", Db.Now())))
        {
            command.ExecuteNonQuery();
        }
        return GetByPath(connection, rootPath)!;
    }

    public RepositoryRecord? Get(long id)
    {
        using var connection = database.Open();
        using var command = connection.Command($"SELECT {Columns} FROM repository WHERE id = @id", null, ("@id", id));
        return command.ReadAll(Map).FirstOrDefault();
    }

    public RepositoryRecord? GetByPath(string rootPath)
    {
        using var connection = database.Open();
        return GetByPath(connection, rootPath);
    }

    private static RepositoryRecord? GetByPath(SqliteConnection connection, string rootPath)
    {
        using var command = connection.Command($"SELECT {Columns} FROM repository WHERE root_path = @root", null, ("@root", rootPath));
        return command.ReadAll(Map).FirstOrDefault();
    }

    public IReadOnlyList<RepositoryRecord> GetRecent(int limit = 50)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            $"SELECT {Columns} FROM repository ORDER BY is_favorite DESC, COALESCE(last_opened_at, created_at) DESC LIMIT @limit",
            null, ("@limit", limit));
        return command.ReadAll(Map);
    }

    public IReadOnlyList<RepositoryRecord> GetByCommonDir(string commonDir)
    {
        using var connection = database.Open();
        using var command = connection.Command($"SELECT {Columns} FROM repository WHERE common_dir = @common", null, ("@common", commonDir));
        return command.ReadAll(Map);
    }

    public void SetFavorite(long id, bool favorite) => Execute("UPDATE repository SET is_favorite = @v WHERE id = @id", id, favorite ? 1 : 0);

    public void SetTimeMachineEnabled(long id, bool enabled) => Execute("UPDATE repository SET time_machine_enabled = @v WHERE id = @id", id, enabled ? 1 : 0);

    /// <summary>Listeden kaldırır. Recovery verisi cascade ile silinir; Git repository'sine dokunulmaz.</summary>
    public void Remove(long id)
    {
        using var connection = database.Open();
        using var command = connection.Command("DELETE FROM repository WHERE id = @id", null, ("@id", id));
        command.ExecuteNonQuery();
    }

    private void Execute(string sql, long id, object value)
    {
        using var connection = database.Open();
        using var command = connection.Command(sql, null, ("@id", id), ("@v", value));
        command.ExecuteNonQuery();
    }

    private static RepositoryRecord Map(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        Db.FromMs(r.GetInt64(6)), r.IsDBNull(7) ? null : Db.FromMs(r.GetInt64(7)), r.GetInt64(8) != 0, r.GetInt64(9) != 0);
}
