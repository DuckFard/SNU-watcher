using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreHistoryPagingTests
{
    [Fact]
    public async Task TuplePagesKeepTheirOriginalHighWaterAcrossNewEventsAndClockRollback()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await StoreHistoryLedgerFixture.SeedAsync(fixture, 30, 3);
        var reader = (IAlertHistoryReader)fixture.Store;
        var first = await reader.ReadPageAsync();
        var selected = first.Items.Select(row => row.EventId).ToList();
        await fixture.ObserveAsync(StoreFixture.Key("012", "HIST.0000"), 119);
        AlertHistoryCursor? cursor = first.NextCursor;
        while (cursor is not null)
        {
            var page = await reader.ReadPageAsync(cursor);
            Assert.Equal(3, page.HighWaterOrdinal); Assert.InRange(page.Items.Length, 1, 25);
            selected.AddRange(page.Items.Select(row => row.EventId)); cursor = page.NextCursor;
        }
        Assert.Equal(90, selected.Count); Assert.Equal(90, selected.Distinct().Count());
        var reloaded = await reader.ReadPageAsync(pageSize: 50);
        Assert.Equal(50, reloaded.Items.Length); Assert.True(reloaded.HasMore);
        Assert.Equal(4, reloaded.HighWaterOrdinal); Assert.Equal(4, reloaded.Items[0].Trigger.Ordinal);
        Assert.True(reloaded.Items[0].Trigger.ObservedAtUtc < first.Items[0].Trigger.ObservedAtUtc);
    }

    [Fact]
    public async Task RecipientPagesPreserveOriginalGenerationAndCurrentLabelAfterResumeAndRemoval()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key(); var watches = new List<SubscriptionSnapshot>();
        for (var index = 0; index < 50; index++) watches.Add(await fixture.AddAsync(key, label: "통계학 " + index));
        await fixture.ObserveAsync(key, 119);
        var reader = (IAlertHistoryReader)fixture.Store;
        var item = Assert.Single((await reader.ReadPageAsync()).Items);
        var changed = watches[0];
        await fixture.Store.ChangeSubscriptionAsync(new PauseSubscription(changed.Id, 1, fixture.Clock.GetUtcNow()));
        await fixture.Store.ChangeSubscriptionAsync(new ResumeSubscription(changed.Id, 2, fixture.Clock.GetUtcNow()));
        await fixture.Store.ChangeSubscriptionAsync(new RenameSubscription(changed.Id, 3, fixture.Clock.GetUtcNow(), "현재 이름"));
        await fixture.Store.ChangeSubscriptionAsync(new RemoveSubscription(changed.Id, 4, fixture.Clock.GetUtcNow()));
        var associations = new List<AlertHistoryRecipient>(); AlertRecipientCursor? cursor = null;
        do
        {
            var page = (await reader.ReadRecipientsAsync(item.EventId, cursor, 7))!;
            Assert.InRange(page.Items.Length, 1, 7); associations.AddRange(page.Items); cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(50, associations.Count);
        Assert.Equal(50, associations.Select(row => row.SubscriptionId).Distinct().Count());
        var historical = Assert.Single(associations, row => row.SubscriptionId == changed.Id);
        Assert.Equal(1, historical.Generation); Assert.Equal("현재 이름", historical.CurrentWatchLabel);
        Assert.Equal(50, (await reader.ReadDetailsAsync(item.EventId))!.Trigger.RecipientCount);
    }
}
