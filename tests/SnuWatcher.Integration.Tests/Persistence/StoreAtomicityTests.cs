using SnuWatcher.Application.Persistence;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreAtomicityTests
{
    [Theory]
    [InlineData(SqliteStoreWriteStage.Section)]
    [InlineData(SqliteStoreWriteStage.Members)]
    [InlineData(SqliteStoreWriteStage.Event)]
    [InlineData(SqliteStoreWriteStage.Recipients)]
    [InlineData(SqliteStoreWriteStage.Outbox)]
    [InlineData(SqliteStoreWriteStage.Receipt)]
    public async Task FailureAtEveryCompletionWriteStageRollsBackAllStateAndRetryCreatesOneEvent(SqliteStoreWriteStage target)
    {
        var armed = false;
        await using var fixture = await StoreFixture.CreateAsync(stage =>
        {
            if (armed && stage == target) throw new InjectedStoreFailure();
        });
        var key = StoreFixture.Key();
        var watch = await fixture.AddAsync(key);
        var issued = await fixture.IssueAsync(key);
        var completion = fixture.Counts(issued, key, 119);
        var prepared = await fixture.PrepareAsync(completion);
        Assert.NotNull(prepared.Effects.Notification);
        armed = true;

        await Assert.ThrowsAsync<InjectedStoreFailure>(() => fixture.Store.TryCommitSectionAsync(prepared, default));
        armed = false;
        await fixture.ReopenAsync();

        var section = await fixture.Store.ReadSectionAsync(key, default);
        Assert.Equal(0, section.Version);
        Assert.Equal(0, section.LatestCompletedOrdinal);
        Assert.Null(section.LatestOutcome);
        Assert.Null(section.LastSuccessfulAtUtc);
        Assert.Null(section.Baseline);
        Assert.Null(Assert.Single((await fixture.Store.FindSubscriptionAsync(watch.Id, default))!.Members).InitializedByAttemptOrdinal);
        Assert.Empty(await fixture.Store.ReadPendingDeliveriesAsync(20, default));

        var retry = await fixture.CommitAsync(completion);
        Assert.Equal(CommitWriteStatus.Committed, retry.Status);
        var eventId = Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(20, default)).EventId;
        Assert.Equal(retry.Receipt!.EventId, eventId);
        var replay = await fixture.Store.TryCommitSectionAsync(prepared, default);
        Assert.Equal(CommitWriteStatus.Replayed, replay.Status);
        Assert.Equal(eventId, replay.Receipt!.EventId);
        Assert.Equal(eventId, Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(20, default)).EventId);
    }

    [Fact]
    public async Task FailedIssuanceDoesNotConsumeThePersistedOrdinal()
    {
        var armed = false;
        await using var fixture = await StoreFixture.CreateAsync(stage =>
        {
            if (armed && stage == SqliteStoreWriteStage.Issued) throw new InjectedStoreFailure();
        });
        var key = StoreFixture.Key();
        armed = true;
        await Assert.ThrowsAsync<InjectedStoreFailure>(() => fixture.IssueAsync(key));
        armed = false;
        await fixture.ReopenAsync();

        var successful = await fixture.IssueAsync(key);
        Assert.Equal(1, successful.Token.Ordinal);
        var watch = await fixture.AddAsync(key);
        Assert.Equal(successful.Token.Ordinal, Assert.Single(watch.Members).ActivationWatermark);
    }

    [Fact]
    public async Task FailedSubscriptionWriteLeavesNeitherWatchMembersNorCatalogRevision()
    {
        var armed = false;
        await using var fixture = await StoreFixture.CreateAsync(stage =>
        {
            if (armed && stage == SqliteStoreWriteStage.Subscription) throw new InjectedStoreFailure();
        });
        var key = StoreFixture.Key();
        var issued = await fixture.IssueAsync(key);
        var command = fixture.NewSection(key);
        var before = await fixture.Store.ReadCommitContextAsync(issued.Token, key, default);
        armed = true;
        await Assert.ThrowsAsync<InjectedStoreFailure>(() => fixture.Store.CreateSubscriptionAsync(command, default));
        armed = false;
        await fixture.ReopenAsync();

        Assert.Null(await fixture.Store.FindSubscriptionAsync(command.Id, default));
        Assert.Empty(await fixture.Store.ListSubscriptionsAsync(default));
        var unchanged = await fixture.Store.ReadCommitContextAsync(issued.Token, key, default);
        Assert.Equal(before.CatalogRevision, unchanged.CatalogRevision);
        Assert.Empty(unchanged.Subscriptions);

        var retry = await fixture.Store.CreateSubscriptionAsync(command, default);
        Assert.Equal(1, retry.DefinitionRevision);
        Assert.Equal(issued.Token.Ordinal, Assert.Single(retry.Members).ActivationWatermark);
        Assert.Equal(before.CatalogRevision + 1,
            (await fixture.Store.ReadCommitContextAsync(issued.Token, key, default)).CatalogRevision);
    }

    [Fact]
    public async Task FailedDeliveryWritePreservesPendingRevisionAndCanBeRetriedAfterReopen()
    {
        var armed = false;
        await using var fixture = await StoreFixture.CreateAsync(stage =>
        {
            if (armed && stage == SqliteStoreWriteStage.Delivery) throw new InjectedStoreFailure();
        });
        var key = StoreFixture.Key();
        await fixture.AddAsync(key);
        await fixture.ObserveAsync(key, 119);
        var pending = Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(20, default));
        var command = new DeliveryResultCommand(pending.EventId, pending.DeliveryRevision,
            DeliveryStatus.OsAccepted, 1, fixture.Clock.GetUtcNow());
        armed = true;
        await Assert.ThrowsAsync<InjectedStoreFailure>(() => fixture.Store.RecordDeliveryResultAsync(command, default));
        armed = false;
        await fixture.ReopenAsync();

        var preserved = Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(20, default));
        Assert.Equal(pending.EventId, preserved.EventId);
        Assert.Equal(pending.DeliveryRevision, preserved.DeliveryRevision);
        Assert.Equal(pending.Attempts, preserved.Attempts);
        Assert.Equal(DeliveryStatus.Pending, preserved.Status);
        Assert.Equal(DeliveryWriteResult.Applied, await fixture.Store.RecordDeliveryResultAsync(command, default));
        Assert.Empty(await fixture.Store.ReadPendingDeliveriesAsync(20, default));
        var terminal = (await fixture.Store.ReadDeliveryContextAsync(pending.EventId, default)).Delivery;
        Assert.Equal(DeliveryStatus.OsAccepted, terminal.Status);
        Assert.Equal(1, terminal.Attempts);
        Assert.Equal(pending.DeliveryRevision + 1, terminal.DeliveryRevision);
    }

    private sealed class InjectedStoreFailure() : Exception("Injected store fault.");
}
