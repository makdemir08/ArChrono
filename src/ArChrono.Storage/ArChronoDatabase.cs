using System.Reflection;
using Microsoft.Data.Sqlite;

namespace ArChrono.Storage;

/// <summary>SQLite bağlantı fabrikası ve migration yöneticisi.</summary>
public sealed class ArChronoDatabase
{
    private readonly string _connectionString;

    public ArChronoDatabase(string databasePath)
    {
        DatabasePath = databasePath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 10,
        }.ToString();
    }

    public string DatabasePath { get; }

    public int SchemaVersion { get; private set; }

    public static int LatestSchemaVersion => LoadMigrations().Count;

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA synchronous = NORMAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    /// <summary>Eksik migration'ları sırayla uygular. Veritabanı daha yeni bir sürümdense hata verir.</summary>
    public void Migrate()
    {
        using var connection = Open();
        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            wal.ExecuteScalar();
        }

        var current = GetUserVersion(connection);
        var migrations = LoadMigrations();
        if (current > migrations.Count)
            throw new InvalidOperationException($"The ArChrono database was created by a newer version (schema {current}, supported {migrations.Count}).");

        for (var version = current + 1; version <= migrations.Count; version++)
        {
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migrations[version - 1];
                command.ExecuteNonQuery();
            }
            using (var setVersion = connection.CreateCommand())
            {
                setVersion.Transaction = transaction;
                setVersion.CommandText = $"PRAGMA user_version = {version};";
                setVersion.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        SchemaVersion = migrations.Count;
    }

    private static int GetUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static List<string> LoadMigrations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string prefix = "ArChrono.Storage.Migrations.";
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            })
            .ToList();
    }
}

internal static class Db
{
    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static long ToMs(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    public static DateTimeOffset FromMs(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime();

    public static SqliteCommand Command(this SqliteConnection connection, string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public static long InsertReturningId(this SqliteConnection connection, string sql, SqliteTransaction? transaction, params (string, object?)[] parameters)
    {
        using var command = connection.Command(sql + "; SELECT last_insert_rowid();", transaction, parameters);
        return (long)command.ExecuteScalar()!;
    }

    public static string? StringOrNull(this SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static long? Int64OrNull(this SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    public static int? Int32OrNull(this SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    public static List<T> ReadAll<T>(this SqliteCommand command, Func<SqliteDataReader, T> map)
    {
        using var reader = command.ExecuteReader();
        var list = new List<T>();
        while (reader.Read()) list.Add(map(reader));
        return list;
    }

    /// <summary>"IN (@p0, @p1…)" için parametre listesi üretir.</summary>
    public static string InClause(SqliteCommand command, IReadOnlyList<string> values, string prefix = "p")
    {
        var names = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            names[i] = $"@{prefix}{i}";
            command.Parameters.AddWithValue(names[i], values[i]);
        }
        return string.Join(", ", names);
    }
}
