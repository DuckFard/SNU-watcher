using Microsoft.Data.Sqlite;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class SqliteFactoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SnuWatcher-factory-" + Guid.NewGuid().ToString("N"));

    public SqliteFactoryTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void VerifiesTheActualNativeEngineBeforeOpeningTheApplicationFile()
    {
        var path = Path.Combine(_directory, "watch.sqlite3");
        var factory = new SqliteConnectionFactory(path);

        Assert.Equal(SqliteEngineIdentity.Expected, factory.EngineIdentity);
        Assert.False(File.Exists(path));
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version(), sqlite_source_id();";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(SqliteEngineIdentity.Expected.Version, reader.GetString(0));
        Assert.Equal(SqliteEngineIdentity.Expected.SourceId, reader.GetString(1));
    }

    [Theory]
    [InlineData("3.51.2", "old-engine")]
    [InlineData("3.54.1", "unexpected-source")]
    public void RejectsAnUnsupportedNativeIdentity(string version, string sourceId) =>
        Assert.Throws<NotSupportedException>(() => new SqliteEngineIdentity(version, sourceId).RequireSupported());

    [Fact]
    public void ConfiguresPrivateCacheForeignKeysAndBoundedCommandTimeout()
    {
        using var connection = new SqliteConnectionFactory(Path.Combine(_directory, "settings.sqlite3")).Open();
        var settings = new SqliteConnectionStringBuilder(connection.ConnectionString);
        Assert.Equal(SqliteCacheMode.Private, settings.Cache);
        Assert.True(settings.ForeignKeys);
        Assert.False(settings.Pooling);
        Assert.Equal(5, connection.DefaultTimeout);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";
        Assert.Equal(1L, command.ExecuteScalar());

        command.CommandText = "CREATE TABLE parent (id INTEGER PRIMARY KEY); CREATE TABLE child (parent_id INTEGER REFERENCES parent(id));";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO child VALUES (999);";
        Assert.Equal(19, Assert.Throws<SqliteException>(() => command.ExecuteNonQuery()).SqliteErrorCode);
    }

    [Fact]
    public void OpeningAFutureDatabaseDoesNotChangeItsSchemaVersionOrJournalMode()
    {
        var path = Path.Combine(_directory, "future.sqlite3");
        var factory = new SqliteConnectionFactory(path);
        using (var connection = factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version=999; CREATE TABLE future_data (value TEXT); INSERT INTO future_data VALUES ('keep');";
            command.ExecuteNonQuery();
        }
        var bytes = File.ReadAllBytes(path);

        using (var connection = factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version;";
            Assert.Equal(999L, command.ExecuteScalar());
            command.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("delete", command.ExecuteScalar());
            command.CommandText = "SELECT value FROM future_data;";
            Assert.Equal("keep", command.ExecuteScalar());
        }

        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void QuotesTheDatabasePathRatherThanTreatingItAsConnectionOptions()
    {
        var path = Path.Combine(_directory, "서울;Mode=Memory.sqlite3");
        using (var connection = new SqliteConnectionFactory(path).Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE saved (value INTEGER); INSERT INTO saved VALUES (42);";
            command.ExecuteNonQuery();
        }
        using var reopened = new SqliteConnectionFactory(path).Open();
        using var read = reopened.CreateCommand();
        read.CommandText = "SELECT value FROM saved;";
        Assert.Equal(42L, read.ExecuteScalar());
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData(":memory:")]
    [InlineData("relative.sqlite3")]
    [InlineData("file:watch.sqlite3?mode=memory")]
    public void RejectsRelativeOrUriDatabasePaths(string path) =>
        Assert.Throws<ArgumentException>(() => new SqliteConnectionFactory(path));

    [Fact]
    public void ReadOnlyModeCannotCreateOrModifyAFile()
    {
        var path = Path.Combine(_directory, "readonly.sqlite3");
        var factory = new SqliteConnectionFactory(path);
        Assert.Throws<SqliteException>(() => factory.Open(SqliteOpenMode.ReadOnly));
        Assert.False(File.Exists(path));
        using (factory.Open()) { }
        using var connection = factory.Open(SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE prohibited (value INTEGER);";
        Assert.Equal(8, Assert.Throws<SqliteException>(() => command.ExecuteNonQuery()).SqliteErrorCode);
        Assert.Throws<ArgumentOutOfRangeException>(() => factory.Open(SqliteOpenMode.Memory));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
