using Microsoft.Data.Sqlite;

namespace SnuWatcher.Infrastructure.Persistence;

/// <summary>
/// Opens bounded, unpooled private-cache connections after verifying the app-local native engine.
/// Opening never creates parent directories, runs migrations or changes the database's journal mode.
/// </summary>
public sealed class SqliteConnectionFactory
{
    private static readonly Lazy<SqliteEngineIdentity> VerifiedEngine = new(VerifyNativeEngine);

    public SqliteConnectionFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!Path.IsPathFullyQualified(databasePath))
            throw new ArgumentException("The database path must be an absolute file path.", nameof(databasePath));
        DatabasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath { get; }

    /// <summary>Verified using an in-memory connection, without opening the application database.</summary>
    public SqliteEngineIdentity EngineIdentity => VerifiedEngine.Value;

    /// <summary>The caller owns the returned connection and must not share it concurrently.</summary>
    public SqliteConnection Open(SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        if (mode is not (SqliteOpenMode.ReadOnly or SqliteOpenMode.ReadWrite or SqliteOpenMode.ReadWriteCreate))
            throw new ArgumentOutOfRangeException(nameof(mode), "Only file database modes are supported.");
        _ = EngineIdentity;
        var connection = new SqliteConnection(Settings(DatabasePath, mode).ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteConnectionStringBuilder Settings(string dataSource, SqliteOpenMode mode) => new()
    {
        DataSource = dataSource,
        Mode = mode,
        Cache = SqliteCacheMode.Private,
        ForeignKeys = true,
        DefaultTimeout = 5,
        Pooling = false,
    };

    private static SqliteEngineIdentity VerifyNativeEngine()
    {
        // Lazy initialization calls the provider bootstrap once. The probe has no on-disk side effects.
        SQLitePCL.Batteries_V2.Init();
        using var connection = new SqliteConnection(Settings(":memory:", SqliteOpenMode.Memory).ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version(), sqlite_source_id();";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("SQLite did not return its native engine identity.");
        return new SqliteEngineIdentity(reader.GetString(0), reader.GetString(1)).RequireSupported();
    }
}
