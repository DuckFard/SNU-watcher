using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreHistoryBoundaryTests
{
    [Fact]
    public async Task LargestValidBatchSequenceIsIncludedOnTheFirstRecordPage()
    {
        using var fixture = new StoreHistoryV4Fixture();
        await using var store = await SqliteWatchStore.OpenAsync(fixture.Factory);
        using (var connection = fixture.Factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE DeliveryBatchAttempts SET sequence=$last,snapshot=json_set(snapshot,'$.sequence',$last) WHERE id=$id;";
            command.Parameters.AddWithValue("$last", long.MaxValue); command.Parameters.AddWithValue("$id", StoreUiV3Fixture.UnresolvedAttemptId);
            command.ExecuteNonQuery();
        }
        var page = await ((IAlertHistoryReader)store).ReadAttemptsAsync(StoreBatchV2Fixture.EventId, pageSize: 1);
        Assert.Equal(long.MaxValue, Assert.Single(page!.Items).Sequence);
        Assert.Equal(AlertAttemptOrigin.BatchAttempt, page.Items[0].Origin);
    }

    [Fact]
    public async Task LargestValidEventResultRevisionIsIncludedOnTheFirstRecordPage()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key(); await fixture.AddAsync(key); await fixture.ObserveAsync(key, 119);
        var history = (IAlertHistoryReader)fixture.Store;
        var item = Assert.Single((await history.ReadPageAsync()).Items);
        await fixture.Store.RecordDeliveryResultAsync(new(item.EventId, 1, DeliveryStatus.OsAccepted, 1, fixture.Clock.GetUtcNow()));
        using (var connection = new SqliteConnectionFactory(fixture.DatabasePath).Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE DeliveryAttempts SET revision=$last; UPDATE Outbox SET revision=$last;";
            command.Parameters.AddWithValue("$last", long.MaxValue); command.ExecuteNonQuery();
        }
        var page = await history.ReadAttemptsAsync(item.EventId);
        Assert.Equal(long.MaxValue, Assert.Single(page!.Items).Sequence);
        Assert.Equal(AlertAttemptOrigin.EventResult, page.Items[0].Origin);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task PageLimitsAndForeignDetailCursorsFailBeforeDatabaseAdmission(int size)
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var reader = (IAlertHistoryReader)fixture.Store;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadPageAsync(pageSize: size));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadRecipientsAsync(Guid.NewGuid(), pageSize: size));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadAttemptsAsync(Guid.NewGuid(), pageSize: size));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadDetailsAsync(Guid.Empty));
        var eventId = Guid.NewGuid(); var foreign = Guid.NewGuid();
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadRecipientsAsync(eventId, new(foreign, Guid.NewGuid(), 1)));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAttemptsAsync(eventId, new(foreign, AlertAttemptOrigin.EventResult, 2)));
        await Assert.ThrowsAsync<AlertHistoryReadException>(() => reader.ReadPageAsync(new(1, 1, eventId)));
    }
}
