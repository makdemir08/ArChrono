using System.Text.Json;
using ArChrono.Storage.Records;
using Microsoft.Data.Sqlite;

namespace ArChrono.Storage.Stores;

public sealed class SettingsStore(ArChronoDatabase database)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public T? Get<T>(string key)
    {
        using var connection = database.Open();
        using var command = connection.Command("SELECT value FROM setting WHERE key = @key", null, ("@key", key));
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json, JsonOptions) : default;
    }

    public void Set<T>(string key, T value)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            "INSERT INTO setting (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            null, ("@key", key), ("@value", JsonSerializer.Serialize(value, JsonOptions)));
        command.ExecuteNonQuery();
    }
}

public sealed class AiRequestStore(ArChronoDatabase database)
{
    public long Insert(AiRequestRecord record)
    {
        using var connection = database.Open();
        return connection.InsertReturningId(
            """
            INSERT INTO ai_request (repository_id, created_at, feature, provider, model, endpoint_host, request_bytes, included_paths_json, excluded_paths_json,
                                    redaction_count, prompt_sha256, status, duration_ms, input_tokens, output_tokens, error_message)
            VALUES (@repo, @created, @feature, @provider, @model, @host, @bytes, @included, @excluded, @redactions, @hash, @status, @duration, @in, @out, @error)
            """, null,
            ("@repo", record.RepositoryId), ("@created", Db.ToMs(record.CreatedAt)), ("@feature", record.Feature), ("@provider", record.Provider),
            ("@model", record.Model), ("@host", record.EndpointHost), ("@bytes", record.RequestBytes),
            ("@included", JsonSerializer.Serialize(record.IncludedPaths)), ("@excluded", JsonSerializer.Serialize(record.ExcludedPaths)),
            ("@redactions", record.RedactionCount), ("@hash", record.PromptSha256), ("@status", record.Status), ("@duration", record.DurationMs),
            ("@in", record.InputTokens), ("@out", record.OutputTokens), ("@error", record.ErrorMessage));
    }

    public IReadOnlyList<AiRequestRecord> List(int limit = 200)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            """
            SELECT id, repository_id, created_at, feature, provider, model, endpoint_host, request_bytes, included_paths_json, excluded_paths_json,
                   redaction_count, prompt_sha256, status, duration_ms, input_tokens, output_tokens, error_message
            FROM ai_request ORDER BY created_at DESC LIMIT @limit
            """, null, ("@limit", limit));
        return command.ReadAll(r => new AiRequestRecord(
            r.GetInt64(0), r.Int64OrNull(1), Db.FromMs(r.GetInt64(2)), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt64(7),
            JsonSerializer.Deserialize<List<string>>(r.GetString(8)) ?? [], JsonSerializer.Deserialize<List<string>>(r.GetString(9)) ?? [],
            r.GetInt32(10), r.GetString(11), r.GetString(12), r.Int64OrNull(13), r.Int32OrNull(14), r.Int32OrNull(15), r.StringOrNull(16)));
    }

    public string? GetCachedResult(string cacheKey)
    {
        using var connection = database.Open();
        using var command = connection.Command("SELECT content FROM ai_result_cache WHERE cache_key = @key", null, ("@key", cacheKey));
        return command.ExecuteScalar() as string;
    }

    public void PutCachedResult(string cacheKey, long? repositoryId, string feature, string subject, string provider, string model, string content)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            """
            INSERT INTO ai_result_cache (cache_key, repository_id, feature, subject, provider, model, content, created_at)
            VALUES (@key, @repo, @feature, @subject, @provider, @model, @content, @now)
            ON CONFLICT (cache_key) DO UPDATE SET content = excluded.content, created_at = excluded.created_at
            """, null,
            ("@key", cacheKey), ("@repo", repositoryId), ("@feature", feature), ("@subject", subject), ("@provider", provider),
            ("@model", model), ("@content", content), ("@now", Db.Now()));
        command.ExecuteNonQuery();
    }
}

public sealed class WorkspaceStore(ArChronoDatabase database)
{
    public WorkspaceRecord Upsert(long repositoryId, string worktreePath, string? branch, string? label, string? agentKind, string? agentCommand)
    {
        using var connection = database.Open();
        using (var command = connection.Command(
                   """
                   INSERT INTO workspace (repository_id, worktree_path, branch, label, agent_kind, agent_command, created_at, last_active_at)
                   VALUES (@repo, @path, @branch, @label, @kind, @cmd, @now, @now)
                   ON CONFLICT (worktree_path) DO UPDATE SET branch = excluded.branch, label = COALESCE(excluded.label, label),
                       agent_kind = COALESCE(excluded.agent_kind, agent_kind), agent_command = COALESCE(excluded.agent_command, agent_command),
                       last_active_at = excluded.last_active_at
                   """, null,
                   ("@repo", repositoryId), ("@path", worktreePath), ("@branch", branch), ("@label", label), ("@kind", agentKind), ("@cmd", agentCommand), ("@now", Db.Now())))
        {
            command.ExecuteNonQuery();
        }
        return List(repositoryId).First(w => w.WorktreePath == worktreePath);
    }

    public IReadOnlyList<WorkspaceRecord> List(long repositoryId)
    {
        using var connection = database.Open();
        using var command = connection.Command(
            "SELECT id, repository_id, worktree_path, branch, label, agent_kind, agent_command, status, created_at, last_active_at FROM workspace WHERE repository_id = @repo ORDER BY created_at",
            null, ("@repo", repositoryId));
        return command.ReadAll(Map);
    }

    private static WorkspaceRecord Map(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.StringOrNull(3), r.StringOrNull(4), r.StringOrNull(5), r.StringOrNull(6), r.GetString(7),
        Db.FromMs(r.GetInt64(8)), r.IsDBNull(9) ? null : Db.FromMs(r.GetInt64(9)));
}

/// <summary>Tüm store'ları bir arada sunar.</summary>
public sealed class ArChronoStorage
{
    public ArChronoStorage(ArChronoDatabase database)
    {
        Database = database;
        Repositories = new RepositoryStore(database);
        Snapshots = new SnapshotStore(database);
        Blobs = new ContentBlobIndex(database);
        RecoveryPoints = new RecoveryPointStore(database);
        Operations = new OperationStore(database);
        Pins = new PinStore(database);
        Settings = new SettingsStore(database);
        AiRequests = new AiRequestStore(database);
        Workspaces = new WorkspaceStore(database);
    }

    public ArChronoDatabase Database { get; }
    public RepositoryStore Repositories { get; }
    public SnapshotStore Snapshots { get; }
    public ContentBlobIndex Blobs { get; }
    public RecoveryPointStore RecoveryPoints { get; }
    public OperationStore Operations { get; }
    public PinStore Pins { get; }
    public SettingsStore Settings { get; }
    public AiRequestStore AiRequests { get; }
    public WorkspaceStore Workspaces { get; }

    public static ArChronoStorage OpenAndMigrate(string databasePath)
    {
        var database = new ArChronoDatabase(databasePath);
        database.Migrate();
        return new ArChronoStorage(database);
    }
}
