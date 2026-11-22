using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreHistoryAssociationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedAttemptCannotBorrowAnotherSectionFromSnapshotOrDurableBatch(bool corruptBatch)
    {
        using var fixture = new StoreHistoryV4Fixture();
        await using var store = await SqliteWatchStore.OpenAsync(fixture.Factory);
        var attemptId = Guid.ParseExact(StoreUiV3Fixture.UnresolvedAttemptId, "N");
        await store.RecordBatchAttemptResultAsync(new(attemptId, BatchAttemptResultOutcome.OsAccepted,
            "history_test_result", DateTimeOffset.Parse(StoreUiV3Fixture.Time)));
        var history = (IAlertHistoryReader)store;
        var original = Assert.Single((await history.ReadAttemptsAsync(StoreBatchV2Fixture.EventId, pageSize: 1))!.Items);
        Assert.True(original.CoversEvent); Assert.Equal(BatchAttemptResultOutcome.OsAccepted, original.BatchAttempt!.Outcome);
        var foreign = StoreFixture.Key("001", "F33.107");
        using (var connection = fixture.Factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = corruptBatch
                ? "INSERT INTO SectionIdentities VALUES($key,$year,$semester,$detail,$course,$section,$subject); UPDATE DeliveryBatches SET sectionKey=$key;"
                : "UPDATE DeliveryBatchAttempts SET snapshot=json_set(snapshot,'$.key',$key) WHERE id=$attempt;";
            command.Parameters.AddWithValue("$key", StoreWire.Key(foreign));
            if (corruptBatch)
            {
                command.Parameters.AddWithValue("$year", foreign.Year); command.Parameters.AddWithValue("$semester", foreign.SemesterCode);
                command.Parameters.AddWithValue("$detail", foreign.DetailSemesterCode); command.Parameters.AddWithValue("$course", foreign.CourseCode);
                command.Parameters.AddWithValue("$section", foreign.Section); command.Parameters.AddWithValue("$subject", foreign.SubsubjectCode);
            }
            else command.Parameters.AddWithValue("$attempt", attemptId.ToString("N"));
            command.ExecuteNonQuery();
        }
        var failure = await Assert.ThrowsAsync<AlertHistoryReadException>(() => history.ReadAttemptsAsync(StoreBatchV2Fixture.EventId));
        Assert.Equal("alert_history_unavailable", failure.DiagnosticCode); Assert.Null(failure.InnerException);
    }

    [Fact]
    public async Task AssociatedBatchReservationForAnotherEventCannotMarkThisEventUnresolved()
    {
        using var fixture = new StoreHistoryV4Fixture();
        await using var store = await SqliteWatchStore.OpenAsync(fixture.Factory);
        var other = Guid.ParseExact("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "N");
        using (var connection = fixture.Factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO IssuedChecks VALUES('abababababababababababababababab',8,$at);
                INSERT INTO IssuedScopes SELECT 'abababababababababababababababab',sectionKey FROM Events WHERE id=$old;
                INSERT INTO Events SELECT $other,sectionKey,8,json_set(trigger,'$.ordinal',8),createdUtc FROM Events WHERE id=$old;
                INSERT INTO EventRecipients SELECT $other,subscriptionId,generation,reason FROM EventRecipients WHERE eventId=$old;
                INSERT INTO Outbox VALUES($other,1,0,0);
                INSERT INTO DeliveryBatchEvents SELECT batchId,$other,1,3 FROM DeliveryBatchEvents WHERE eventId=$old;
                DELETE FROM DeliveryBatchAttemptEvents WHERE attemptId=$unresolved;
                INSERT INTO DeliveryBatchAttemptEvents VALUES($unresolved,$other,8,1,0);
                UPDATE DeliveryBatchAttempts SET snapshot=json_set(snapshot,'$.ordinal',8,'$.coverage.min',8,'$.coverage.max',8) WHERE id=$unresolved;
                UPDATE DeliveryBatches SET foldCutoff=8,foldCursor=8;
                UPDATE StoreMetadata SET issuedOrdinal=8 WHERE id=1;
                """;
            command.Parameters.AddWithValue("$at", StoreUiV3Fixture.Time);
            command.Parameters.AddWithValue("$old", StoreBatchV2Fixture.EventId.ToString("N"));
            command.Parameters.AddWithValue("$other", other.ToString("N"));
            command.Parameters.AddWithValue("$unresolved", StoreUiV3Fixture.UnresolvedAttemptId); command.ExecuteNonQuery();
        }
        var history = (IAlertHistoryReader)store;
        Assert.Null((await history.ReadDetailsAsync(StoreBatchV2Fixture.EventId))!.Delivery.UnresolvedAttemptId);
        Assert.NotNull((await history.ReadDetailsAsync(other))!.Delivery.UnresolvedAttemptId);
        var associated = Assert.Single((await history.ReadAttemptsAsync(StoreBatchV2Fixture.EventId, pageSize: 1))!.Items);
        Assert.False(associated.CoversEvent); Assert.Null(associated.BatchAttempt!.Outcome);
        Assert.Equal(8, associated.BatchAttempt.Content.CurrentSourceOrdinal);
        Assert.Equal(7, (await history.ReadDetailsAsync(StoreBatchV2Fixture.EventId))!.Trigger.Ordinal);
    }

    [Theory]
    [InlineData(BatchAttemptResultOutcome.NotSubmitted, DeliveryStatus.Pending, 1)]
    [InlineData(BatchAttemptResultOutcome.InterruptedUnknown, DeliveryStatus.Pending, 2)]
    [InlineData(BatchAttemptResultOutcome.RetryableFailure, DeliveryStatus.Pending, 2)]
    [InlineData(BatchAttemptResultOutcome.OsAccepted, DeliveryStatus.OsAccepted, 2)]
    public async Task DurableBatchResultOutcomeAndPerEventBudgetRemainDistinct(BatchAttemptResultOutcome outcome, DeliveryStatus status, int attempts)
    {
        using var fixture = new StoreHistoryV4Fixture();
        await using var store = await SqliteWatchStore.OpenAsync(fixture.Factory);
        await store.RecordBatchAttemptResultAsync(new(Guid.ParseExact(StoreUiV3Fixture.UnresolvedAttemptId, "N"), outcome,
            "history_test_result", DateTimeOffset.Parse(StoreUiV3Fixture.Time)));
        var history = (IAlertHistoryReader)store;
        var item = (await history.ReadDetailsAsync(StoreBatchV2Fixture.EventId))!;
        Assert.Equal(status, item.Delivery.Status); Assert.Equal(attempts, item.Delivery.Attempts);
        Assert.Null(item.Delivery.UnresolvedAttemptId);
        var record = Assert.Single((await history.ReadAttemptsAsync(item.EventId, pageSize: 1))!.Items);
        Assert.Equal(outcome, record.BatchAttempt!.Outcome); Assert.NotNull(record.BatchAttempt.RecordedAtUtc);
        Assert.Equal(1, item.Trigger.CurrentSeats);
    }
}
