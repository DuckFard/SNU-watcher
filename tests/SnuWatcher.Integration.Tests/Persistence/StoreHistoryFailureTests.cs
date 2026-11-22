using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreHistoryFailureTests
{
    [Theory]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    [InlineData("seats-type")]
    [InlineData("ordinal-mismatch")]
    [InlineData("key-mismatch")]
    [InlineData("array-type")]
    [InlineData("oversize")]
    [InlineData("status")]
    [InlineData("reason")]
    [InlineData("missing-outbox")]
    public async Task CorruptEventStorageProducesOneFixedTypedFailureWithoutPartialRowsOrRawDetails(string kind)
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key(); await fixture.AddAsync(key); await fixture.ObserveAsync(key, 119);
        using (var connection = new SqliteConnectionFactory(fixture.DatabasePath).Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = kind switch
            {
                "malformed" or "duplicate" or "oversize" => "UPDATE Events SET trigger=$json;",
                "seats-type" => "UPDATE Events SET trigger=json_set(trigger,'$.seats','private-path-value');",
                "ordinal-mismatch" => "UPDATE Events SET trigger=json_set(trigger,'$.ordinal',999);",
                "key-mismatch" => "UPDATE Events SET trigger=json_set(trigger,'$.key','[2026,\"U000200002\",\"U000300002\",\"OTHER\",\"001\",\"0\"]');",
                "array-type" => "UPDATE Events SET trigger=json_set(trigger,'$.recipients',123);",
                "status" => "UPDATE Outbox SET status=99;",
                "reason" => "UPDATE EventRecipients SET reason=99;",
                "missing-outbox" => "DELETE FROM Outbox;",
                _ => throw new ArgumentException("Unknown test corruption."),
            };
            if (kind is "malformed" or "duplicate" or "oversize")
            {
                using var original = connection.CreateCommand(); original.CommandText = "SELECT trigger FROM Events;";
                var json = (string)original.ExecuteScalar()!;
                command.Parameters.AddWithValue("$json", kind switch
                {
                    "malformed" => "{ private database filename", "duplicate" => json.Insert(1, "\"seats\":7,"),
                    _ => json + new string(' ', 4 * 1024 * 1024),
                });
            }
            command.ExecuteNonQuery();
        }
        var error = await Assert.ThrowsAsync<AlertHistoryReadException>(() => ((IAlertHistoryReader)fixture.Store).ReadPageAsync());
        Assert.Equal("alert_history_unavailable", error.DiagnosticCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.DatabasePath, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("UPDATE DeliveryBatchAttempts SET snapshot=json_set(snapshot,'$.version',999);")]
    [InlineData("UPDATE DeliveryBatchAttempts SET snapshot=json_set(snapshot,'$.id','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb');")]
    [InlineData("UPDATE DeliveryBatchAttempts SET snapshot=json_set(snapshot,'$.recipients',123);")]
    [InlineData("UPDATE DeliveryBatchResults SET sourceFacts=json_set(sourceFacts,'$.outcome',1);")]
    [InlineData("UPDATE DeliveryBatchResults SET receipt=json_set(receipt,'$.accepted',1);")]
    [InlineData("UPDATE DeliveryBatchAttempts SET status=1 WHERE sequence=2;")]
    public async Task CorruptAssociatedBatchFactsCannotBecomeAnAcceptedOrInventedAttempt(string sql)
    {
        using var fixture = new StoreHistoryV4Fixture();
        await using var store = await SqliteWatchStore.OpenAsync(fixture.Factory);
        fixture.Alter(sql);
        var error = await Assert.ThrowsAsync<AlertHistoryReadException>(() =>
            ((IAlertHistoryReader)store).ReadAttemptsAsync(StoreBatchV2Fixture.EventId));
        Assert.Equal("alert_history_unavailable", error.DiagnosticCode);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task CancellationAndClosedAdmissionRemainLifecycleResults()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var reader = (IAlertHistoryReader)fixture.Store;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadPageAsync(cancellationToken: cancelled.Token));
        await fixture.Store.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadPageAsync());
    }

    [Fact]
    public async Task CorruptIssuedHighWaterCannotSilentlyPresentAnExistingLedgerAsEmpty()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key(); await fixture.AddAsync(key); await fixture.ObserveAsync(key, 119);
        using (var connection = new SqliteConnectionFactory(fixture.DatabasePath).Open())
        using (var command = connection.CreateCommand())
        { command.CommandText = "UPDATE StoreMetadata SET issuedOrdinal=0;"; command.ExecuteNonQuery(); }
        await Assert.ThrowsAsync<AlertHistoryReadException>(() => ((IAlertHistoryReader)fixture.Store).ReadPageAsync());
    }
}
