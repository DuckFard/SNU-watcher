using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreHistoryReaderTests
{
    private static IAlertHistoryReader Reader(SqliteWatchStore store) => (IAlertHistoryReader)(object)store;

    [Fact]
    public async Task HistoricalOpeningFactsRemainSeparateFromCurrentDurableDeliveryAndRenamedWatchLabel()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        var watch = await fixture.AddAsync(key, label: "통계학");
        await fixture.ObserveAsync(key, 120);
        await fixture.ObserveAsync(key, 119);
        var page = await Reader(fixture.Store).ReadPageAsync();
        var opening = Assert.Single(page.Items);
        Assert.Equal(key, opening.Trigger.Key);
        Assert.Equal("012", opening.Trigger.Key.Section);
        Assert.Equal(1, opening.Trigger.CurrentSeats);
        Assert.Equal(0, opening.Trigger.PreviousSeats);
        Assert.Equal(1, opening.Trigger.RecipientCount);
        Assert.True(opening.Trigger.HasIncreaseReason);
        Assert.Equal(DeliveryStatus.Pending, opening.Delivery.Status);
        Assert.Null(opening.Delivery.LastRecordedResult);
        var observed = opening.Trigger.ObservedAtUtc;
        await fixture.Store.RecordDeliveryResultAsync(new(opening.EventId, opening.Delivery.Revision,
            DeliveryStatus.OsAccepted, 1, fixture.Clock.GetUtcNow()));
        await fixture.Store.ChangeSubscriptionAsync(new RenameSubscription(watch.Id, watch.DefinitionRevision,
            fixture.Clock.GetUtcNow(), "새 이름"));
        fixture.Clock.Advance(TimeSpan.FromHours(-1));
        await fixture.ObserveAsync(key, 120);
        var current = await Reader(fixture.Store).ReadDetailsAsync(opening.EventId);
        Assert.Equal(1, current!.Trigger.CurrentSeats);
        Assert.Equal(observed, current.Trigger.ObservedAtUtc);
        Assert.Equal(DeliveryStatus.OsAccepted, current.Delivery.Status);
        Assert.Equal(1, current.Delivery.Attempts);
        Assert.NotNull(current.Delivery.LastRecordedResult);
        var recipient = Assert.Single((await Reader(fixture.Store).ReadRecipientsAsync(opening.EventId))!.Items);
        Assert.Equal(watch.Id, recipient.SubscriptionId);
        Assert.Equal(OpeningReason.IncreasedSeats, recipient.Reason);
        Assert.Equal("새 이름", recipient.CurrentWatchLabel);
    }

    [Fact]
    public async Task GenuineBatchLedgerReportsUnresolvedReservationWithoutManufacturingOsAcceptance()
    {
        using var fixture = new StoreHistoryV4Fixture();
        await using var store = await SqliteWatchStore.OpenAsync(fixture.Factory);
        var item = Assert.Single((await Reader(store).ReadPageAsync()).Items);
        Assert.Equal(DeliveryStatus.Pending, item.Delivery.Status);
        Assert.Equal(Guid.ParseExact(StoreUiV3Fixture.BatchId, "N"), item.Delivery.BatchId);
        Assert.Equal(Guid.ParseExact(StoreUiV3Fixture.UnresolvedAttemptId, "N"), item.Delivery.UnresolvedAttemptId);
        var first = await Reader(store).ReadAttemptsAsync(item.EventId, pageSize: 1);
        var unresolved = Assert.Single(first!.Items);
        Assert.Equal(AlertAttemptOrigin.BatchAttempt, unresolved.Origin);
        Assert.Null(unresolved.BatchAttempt!.Outcome);
        Assert.Null(unresolved.BatchAttempt.RecordedAtUtc);
        Assert.True(unresolved.CoversEvent);
        Assert.True(first.HasMore);
        var second = await Reader(store).ReadAttemptsAsync(item.EventId, first.NextCursor, 1);
        Assert.NotNull(Assert.Single(second!.Items).BatchAttempt!.Outcome);
        var third = await Reader(store).ReadAttemptsAsync(item.EventId, second.NextCursor, 1);
        var result = Assert.Single(third!.Items);
        Assert.Equal(AlertAttemptOrigin.EventResult, result.Origin);
        Assert.Equal(DeliveryStatus.Pending, result.EventResult!.Status);
        Assert.Null(result.BatchAttempt);
        Assert.False(third.HasMore);
    }

    [Fact]
    public async Task MissingEventAndEmptyLedgerDoNotProduceFabricatedHistory()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var reader = Reader(fixture.Store);
        var page = await reader.ReadPageAsync();
        Assert.Empty(page.Items); Assert.False(page.HasMore); Assert.Null(page.NextCursor);
        var absent = Guid.NewGuid();
        Assert.Null(await reader.ReadDetailsAsync(absent));
        Assert.Null(await reader.ReadRecipientsAsync(absent));
        Assert.Null(await reader.ReadAttemptsAsync(absent));
    }
}
