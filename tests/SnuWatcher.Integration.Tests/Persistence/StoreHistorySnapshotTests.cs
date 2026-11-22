using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreHistorySnapshotTests
{
    [Fact]
    public async Task AllHistoryReadMethodsLeaveSavedStateDataVersionUnchanged()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key(); await fixture.AddAsync(key); await fixture.ObserveAsync(key, 119);
        using var observer = new SqliteConnectionFactory(fixture.DatabasePath).Open();
        var before = Scalar(observer, "PRAGMA data_version;");
        var reader = (IAlertHistoryReader)fixture.Store;
        var item = Assert.Single((await reader.ReadPageAsync()).Items);
        await reader.ReadDetailsAsync(item.EventId);
        await reader.ReadRecipientsAsync(item.EventId);
        await reader.ReadAttemptsAsync(item.EventId);
        Assert.Equal(before, Scalar(observer, "PRAGMA data_version;"));
    }

    [Fact]
    public async Task OneReadTransactionKeepsMetadataAndStatusConsistentAcrossAnotherWriterCommit()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key(); await fixture.AddAsync(key); await fixture.ObserveAsync(key, 119);
        var original = Assert.Single((await ((IAlertHistoryReader)fixture.Store).ReadPageAsync()).Items);
        var clock = new CallbackClock(fixture.Clock.GetUtcNow());
        await using var readerStore = await SqliteWatchStore.OpenAsync(new(fixture.DatabasePath), clock);
        clock.OnRead = () =>
        {
            using var writer = new SqliteConnectionFactory(fixture.DatabasePath).Open();
            using var command = writer.CreateCommand();
            command.CommandText = "UPDATE Outbox SET revision=2,status=1,attempts=1; "
                + "INSERT INTO DeliveryAttempts SELECT eventId,2,1,1,$time FROM Outbox;";
            command.Parameters.AddWithValue("$time", StoreWire.At(fixture.Clock.GetUtcNow())); command.ExecuteNonQuery();
        };
        var anchored = await ((IAlertHistoryReader)readerStore).ReadDetailsAsync(original.EventId);
        Assert.Equal(DeliveryStatus.Pending, anchored!.Delivery.Status);
        var next = await ((IAlertHistoryReader)readerStore).ReadDetailsAsync(original.EventId);
        Assert.Equal(DeliveryStatus.OsAccepted, next!.Delivery.Status);
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private sealed class CallbackClock(DateTimeOffset now) : TimeProvider
    {
        internal Action? OnRead;
        public override DateTimeOffset GetUtcNow() { Interlocked.Exchange(ref OnRead, null)?.Invoke(); return now; }
    }
}
