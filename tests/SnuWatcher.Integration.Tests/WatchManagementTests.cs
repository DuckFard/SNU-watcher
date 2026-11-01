using SnuWatcher.Application.Delivery;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Watches;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Persistence;
using SnuWatcher.Integration.Tests.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests;

public sealed class WatchManagementTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    [Theory]
    [InlineData("pause", WatchStatus.Paused)]
    [InlineData("remove", WatchStatus.Removed)]
    [InlineData("expiry", WatchStatus.Expired)]
    public async Task LifecycleMutationHoldsGateThroughSqliteCommitThenObsoleteSendCannotBegin(string change, WatchStatus expected)
    {
        var armed = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        await using var fixture = await StoreFixture.CreateAsync(stage =>
        {
            if (!armed || stage != SqliteStoreWriteStage.Subscription) return;
            entered.TrySetResult();
            if (!release.Wait(Timeout)) throw new TimeoutException("Mutation checkpoint was not released.");
        });
        var watch = await fixture.AddAsync(StoreFixture.Key());
        await fixture.ObserveAsync(StoreFixture.Key(), 116);
        watch = (await fixture.Store.FindSubscriptionAsync(watch.Id, default))!;
        var pending = Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(10, default));
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new RecordingChannel();
        var service = new WatchManagementService(fixture.Store, gate);
        var coordinator = new DeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel);
        armed = true;

        var mutation = service.ChangeAsync(Change(change, watch, fixture.Clock.GetUtcNow()));
        await entered.Task.WaitAsync(Timeout);
        var delivery = coordinator.TryDeliverAsync(pending.EventId, MaxAge, 3);
        try
        {
            Assert.False(mutation.IsCompleted);
            Assert.False(delivery.IsCompleted);
            Assert.Empty(channel.Envelopes);
        }
        finally { release.Set(); }

        var changed = await mutation.WaitAsync(Timeout);
        var result = await delivery.WaitAsync(Timeout);
        Assert.Equal(expected, changed.Status);
        Assert.Equal(DeliveryDisposition.Superseded, result.Decision.Disposition);
        Assert.Empty(channel.Envelopes);
        await fixture.ReopenAsync();
        Assert.Equal(expected, (await fixture.Store.FindSubscriptionAsync(watch.Id, default))!.Status);
        Assert.Equal(DeliveryStatus.Superseded, (await fixture.Store.ReadDeliveryContextAsync(pending.EventId, default)).Delivery.Status);
    }

    [Fact]
    public async Task PauseWaitsForInflightSendAndItsRecordedResultBeforeReturning()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var watch = await fixture.AddAsync(StoreFixture.Key());
        await fixture.ObserveAsync(StoreFixture.Key(), 116);
        var pending = Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(10, default));
        watch = (await fixture.Store.FindSubscriptionAsync(watch.Id, default))!;
        using var gate = new SemaphoreSlim(1, 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<DesktopAlertOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new RecordingChannel { Send = (_, _) => { started.SetResult(); return release.Task; } };
        var coordinator = new DeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel);
        var delivery = coordinator.TryDeliverAsync(pending.EventId, MaxAge, 3);
        await started.Task.WaitAsync(Timeout);
        var pause = new WatchManagementService(fixture.Store, gate).ChangeAsync(
            new PauseSubscription(watch.Id, watch.DefinitionRevision, fixture.Clock.GetUtcNow()));
        try
        {
            Assert.False(pause.IsCompleted);
            Assert.Equal(WatchStatus.Active, (await fixture.Store.FindSubscriptionAsync(watch.Id, default))!.Status);
        }
        finally { release.TrySetResult(DesktopAlertOutcome.RetryableFailure); }

        await Task.WhenAll(delivery, pause).WaitAsync(Timeout);
        var paused = await pause;
        Assert.Equal(1, (await fixture.Store.ReadDeliveryContextAsync(pending.EventId, default)).Delivery.Attempts);
        Assert.Equal(WatchStatus.Paused, paused.Status);
        Assert.Equal(DeliveryDisposition.Superseded,
            (await coordinator.TryDeliverAsync(pending.EventId, MaxAge, 3)).Decision.Disposition);
        Assert.Single(channel.Envelopes);
    }

    [Fact]
    public async Task ResumePreservesSharedBaselineAndOtherWatchGenerationWhileExcludingOldRecipient()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        using var gate = new SemaphoreSlim(1, 1);
        var service = new WatchManagementService(fixture.Store, gate);
        var key = StoreFixture.Key();
        var first = await service.CreateAsync(fixture.NewSection(key));
        var second = await service.CreateAsync(fixture.NewSection(key));
        await fixture.ObserveAsync(key, 116);
        var baseline = await fixture.Store.ReadSectionAsync(key, default);
        first = (await fixture.Store.FindSubscriptionAsync(first.Id, default))!;
        second = (await fixture.Store.FindSubscriptionAsync(second.Id, default))!;
        var pending = Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(10, default));
        var paused = await service.ChangeAsync(new PauseSubscription(first.Id, first.DefinitionRevision, fixture.Clock.GetUtcNow()));
        var inflight = await fixture.IssueAsync(key);

        var resumed = await service.ChangeAsync(new ResumeSubscription(paused.Id, paused.DefinitionRevision, fixture.Clock.GetUtcNow()));

        Assert.Equal(first.ActivationGeneration + 1, resumed.ActivationGeneration);
        Assert.Null(Assert.Single(resumed.Members).InitializedByAttemptOrdinal);
        Assert.Equal(inflight.Token.Ordinal, Assert.Single(resumed.Members).ActivationWatermark);
        Assert.Equal(baseline, await fixture.Store.ReadSectionAsync(key, default));
        var unchanged = (await fixture.Store.FindSubscriptionAsync(second.Id, default))!;
        Assert.Equal(second.DefinitionRevision, unchanged.DefinitionRevision);
        Assert.Equal(second.ActivationGeneration, unchanged.ActivationGeneration);
        Assert.Equal(second.Members.ToArray(), unchanged.Members.ToArray());
        var channel = new RecordingChannel();
        await new DeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel).TryDeliverAsync(pending.EventId, MaxAge, 3);
        var recipient = Assert.Single(Assert.Single(channel.Envelopes).EligibleRecipients);
        Assert.Equal(second.Id.ToString("N"), recipient.SubscriptionId);
        Assert.Equal(second.ActivationGeneration, recipient.Generation);
    }

    [Fact]
    public async Task OptimisticConflictPropagatesAndDoesNotPreventLaterValidMutation()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        using var gate = new SemaphoreSlim(1, 1);
        var service = new WatchManagementService(fixture.Store, gate);
        var watch = await service.CreateAsync(fixture.NewSection(StoreFixture.Key()));
        var renamed = await service.ChangeAsync(new RenameSubscription(watch.Id, watch.DefinitionRevision,
            fixture.Clock.GetUtcNow(), "Renamed"));

        await Assert.ThrowsAsync<StoreVersionConflictException>(() => service.ChangeAsync(
            new RemoveSubscription(watch.Id, watch.DefinitionRevision, fixture.Clock.GetUtcNow())));
        var paused = await service.ChangeAsync(new PauseSubscription(watch.Id, renamed.DefinitionRevision,
            fixture.Clock.GetUtcNow())).WaitAsync(Timeout);

        Assert.Equal(WatchStatus.Paused, paused.Status);
        Assert.Equal("Renamed", paused.Label);
        Assert.Equal(renamed.DefinitionRevision + 1, paused.DefinitionRevision);
    }

    [Fact]
    public async Task CancellationWhileWaitingLeavesSqliteStateUnchangedAndGateUsable()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var watch = await fixture.AddAsync(StoreFixture.Key());
        using var gate = new SemaphoreSlim(0, 1);
        using var cancellation = new CancellationTokenSource();
        var service = new WatchManagementService(fixture.Store, gate);
        var command = new PauseSubscription(watch.Id, watch.DefinitionRevision, fixture.Clock.GetUtcNow());
        var mutation = service.ChangeAsync(command, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation);
        Assert.Equal(WatchStatus.Active, (await fixture.Store.FindSubscriptionAsync(watch.Id, default))!.Status);
        Assert.Equal(0, gate.CurrentCount);
        gate.Release();
        Assert.Equal(WatchStatus.Paused, (await service.ChangeAsync(command).WaitAsync(Timeout)).Status);
    }

    private static SubscriptionChange Change(string change, SubscriptionSnapshot watch, DateTimeOffset now) => change switch
    {
        "pause" => new PauseSubscription(watch.Id, watch.DefinitionRevision, now),
        "remove" => new RemoveSubscription(watch.Id, watch.DefinitionRevision, now),
        _ => new SetSubscriptionExpiry(watch.Id, watch.DefinitionRevision, now,
            WatchExpiry.FromLocalDate(new DateOnly(2026, 10, 31), TimeZoneInfo.Utc)),
    };

    private sealed class RecordingChannel : IDesktopAlertChannel
    {
        public List<DeliveryEnvelope> Envelopes { get; } = [];
        public Func<DeliveryEnvelope, CancellationToken, Task<DesktopAlertOutcome>> Send { get; init; } =
            (_, _) => Task.FromResult(DesktopAlertOutcome.OsAccepted);

        public Task<DesktopAlertOutcome> SendAsync(DeliveryEnvelope envelope, CancellationToken cancellationToken)
        {
            Envelopes.Add(envelope);
            return Send(envelope, cancellationToken);
        }
    }
}
