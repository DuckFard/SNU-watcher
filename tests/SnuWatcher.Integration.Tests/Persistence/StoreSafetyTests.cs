using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreSafetyTests
{
    [Fact]
    public async Task FutureSchemaIsRejectedWithoutChangingItsBytesOrJournal()
    {
        using var directory = new StoreSafetyDirectory();
        var factory = new SqliteConnectionFactory(directory.DatabasePath);
        using (var connection = factory.Open())
        {
            Execute(connection, "PRAGMA application_id=1397642583; PRAGMA user_version=999; CREATE TABLE future_data(value TEXT); INSERT INTO future_data VALUES ('keep');");
        }
        var before = SHA256.HashData(await File.ReadAllBytesAsync(directory.DatabasePath));

        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteWatchStore.OpenAsync(factory));

        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(directory.DatabasePath)));
        using var verify = factory.Open(SqliteOpenMode.ReadOnly);
        Assert.Equal("delete", Scalar(verify, "PRAGMA journal_mode;"));
        Assert.Equal("keep", Scalar(verify, "SELECT value FROM future_data;"));
    }

    [Fact]
    public async Task UnrecognizedExistingDatabaseIsNeverRepurposed()
    {
        using var directory = new StoreSafetyDirectory();
        var factory = new SqliteConnectionFactory(directory.DatabasePath);
        using (var connection = factory.Open()) Execute(connection, "CREATE TABLE unrelated(value TEXT); INSERT INTO unrelated VALUES ('keep');");
        var before = SHA256.HashData(await File.ReadAllBytesAsync(directory.DatabasePath));

        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteWatchStore.OpenAsync(factory));

        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(directory.DatabasePath)));
    }

    [Fact]
    public async Task InitialSchemaFailureRollsBackAllDdlAndVersionMarkers()
    {
        using var directory = new StoreSafetyDirectory();
        var factory = new SqliteConnectionFactory(directory.DatabasePath);

        await Assert.ThrowsAsync<StoreInjectedFailure>(() => SqliteWatchStore.OpenAsync(factory,
            faultInjector: stage => { if (stage == SqliteStoreWriteStage.SchemaCreated) throw new StoreInjectedFailure(); }));

        using var verify = factory.Open();
        Assert.Equal(0L, Scalar(verify, "PRAGMA user_version;"));
        Assert.Equal(0L, Scalar(verify, "PRAGMA application_id;"));
        Assert.Equal(0L, Scalar(verify, "SELECT count(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';"));
    }

    [Fact]
    public async Task RecognizedStoreUsesWalForeignKeysAndFullDurability()
    {
        using var directory = new StoreSafetyDirectory();
        var factory = new SqliteConnectionFactory(directory.DatabasePath);
        await using var store = await SqliteWatchStore.OpenAsync(factory);

        using var verify = factory.Open();
        Assert.Equal(5L, Scalar(verify, "PRAGMA user_version;"));
        Assert.Equal(1397642583L, Scalar(verify, "PRAGMA application_id;"));
        Assert.Equal("wal", Scalar(verify, "PRAGMA journal_mode;"));
        Assert.Equal(1L, Scalar(verify, "PRAGMA foreign_keys;"));
        Assert.Equal(2L, Scalar(verify, "PRAGMA synchronous;"));
    }

    // Upper bounds only: the fixture normally finishes in milliseconds, but each store's worker runs on the
    // thread pool and the first one is deliberately blocked, so a loaded CI runner can delay the second.
    private static readonly TimeSpan FixtureDeadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ConcurrentFreshOpenRechecksSchemaAfterAcquiringTheWriteTransaction()
    {
        using var directory = new StoreSafetyDirectory();
        var factory = new SqliteConnectionFactory(directory.DatabasePath);
        using var releaseFirst = new ManualResetEventSlim();
        var firstCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondValidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTask = SqliteWatchStore.OpenAsync(factory, faultInjector: stage =>
        {
            if (stage != SqliteStoreWriteStage.SchemaCreated) return;
            firstCreated.SetResult();
            if (!releaseFirst.Wait(FixtureDeadline)) throw new TimeoutException("Concurrent schema fixture was not released.");
        });
        Task<SqliteWatchStore>? secondTask = null;
        try
        {
            await firstCreated.Task.WaitAsync(FixtureDeadline);
            secondTask = SqliteWatchStore.OpenAsync(factory, faultInjector: stage =>
            {
                if (stage != SqliteStoreWriteStage.SchemaValidated) return;
                secondValidated.TrySetResult();
                // The second opener has read the empty schema and next waits for the write reservation.
                // Releasing here keeps that wait independent of when the test continuation is scheduled.
                releaseFirst.Set();
            });
            await secondValidated.Task.WaitAsync(FixtureDeadline);
        }
        catch
        {
            // Both openers may still be using watch.db. Close them before the directory is removed, so the
            // original failure is reported instead of a locked-file cleanup error.
            releaseFirst.Set();
            await CloseAfterFixtureFailureAsync(firstTask);
            if (secondTask is not null) await CloseAfterFixtureFailureAsync(secondTask);
            throw;
        }
        await using var first = await firstTask;
        await using var second = await secondTask;
        Assert.Empty(await first.ListSubscriptionsAsync(default));
        Assert.Empty(await second.ListSubscriptionsAsync(default));
    }

    private static async Task CloseAfterFixtureFailureAsync(Task<SqliteWatchStore> opening)
    {
        try { await (await opening.WaitAsync(FixtureDeadline)).DisposeAsync(); }
        catch (Exception) { /* The caller rethrows the fixture failure; an opener that failed holds no store. */ }
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class StoreInjectedFailure : Exception;

    private sealed class StoreSafetyDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "SnuWatcher-store-safety-" + Guid.NewGuid().ToString("N"));

        public StoreSafetyDirectory() => Directory.CreateDirectory(_root);
        public string DatabasePath => Path.Combine(_root, "watch.db");
        public void Dispose()
        {
            var target = Path.GetFullPath(_root);
            if (!string.Equals(Path.GetDirectoryName(target), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(target).StartsWith("SnuWatcher-store-safety-", StringComparison.Ordinal)
                || !Guid.TryParseExact(Path.GetFileName(target)["SnuWatcher-store-safety-".Length..], "N", out _))
                throw new InvalidOperationException("Refusing cleanup outside this fixture's temporary directory.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }
}
