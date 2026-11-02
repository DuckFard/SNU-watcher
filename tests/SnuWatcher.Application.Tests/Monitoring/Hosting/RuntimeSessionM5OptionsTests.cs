using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Tests.Delivery.Batches;
using SnuWatcher.Application.Tests.Monitoring.Scheduling;
using Xunit;

namespace SnuWatcher.Application.Tests.Monitoring.Hosting;

public sealed class RuntimeSessionM5OptionsTests
{
    [Fact]
    public void ImmutableProfileAndDeliveryCopiesPreserveEveryResourceBoundAndSoleMonitorRevision()
    {
        var monitor = new WatchMonitorOptions(WatchMonitorProfile.LowActivity, 2, 53, 7, 8,
            TimeSpan.FromSeconds(11), TimeSpan.FromMinutes(9), TimeSpan.FromSeconds(12), initialPreferencesRevision: 4);
        var delivery = new BatchDeliveryPolicy(TimeSpan.FromMinutes(3), 8);
        var pump = new BatchDeliveryPumpOptions(maxWorkPerTurn: 6);
        var original = new RuntimeSessionOptions(monitor, delivery, pump, 19, 37, 17, 5, 11);
        Assert.True(original.DeliveryEnabled);
        Assert.Equal(4, original.InitialPreferencesRevision);
        var disabled = original.WithDeliveryEnabled(false);
        var changed = disabled.WithInitialRefreshProfile(WatchMonitorProfile.Faster, 9);
        Assert.False(changed.DeliveryEnabled);
        Assert.Equal(9, changed.InitialPreferencesRevision);
        Assert.Equal(WatchMonitorProfile.Faster, changed.Monitor.Profile);
        Assert.Equal(4, original.InitialPreferencesRevision);
        Assert.Equal(WatchMonitorProfile.LowActivity, original.Monitor.Profile);
        Assert.Same(monitor, disabled.Monitor);
        Assert.Same(delivery, changed.Delivery);
        Assert.Same(pump, changed.Pump);
        Assert.Equal(original.MaxAdmittedCommands, changed.MaxAdmittedCommands);
        Assert.Equal(original.MaxRefreshSections, changed.MaxRefreshSections);
        Assert.Equal(original.MaxPendingKnownResults, changed.MaxPendingKnownResults);
        Assert.Equal(original.FinalizeFlushPasses, changed.FinalizeFlushPasses);
        Assert.Equal(original.RecoveryPageSize, changed.RecoveryPageSize);
        Assert.Equal(monitor.MaxConcurrentQueries, changed.Monitor.MaxConcurrentQueries);
        Assert.Equal(monitor.MaxUniqueSections, changed.Monitor.MaxUniqueSections);
        Assert.Equal(monitor.MaxCommitAttempts, changed.Monitor.MaxCommitAttempts);
        Assert.Equal(monitor.JitterPercent, changed.Monitor.JitterPercent);
        Assert.Equal(monitor.RecoveryStagger, changed.Monitor.RecoveryStagger);
        Assert.Equal(monitor.CatalogRefreshInterval, changed.Monitor.CatalogRefreshInterval);
        Assert.Equal(monitor.CommitRetryDelay, changed.Monitor.CommitRetryDelay);
        Assert.True(changed.WithDeliveryEnabled(true).DeliveryEnabled);
        Assert.Throws<ArgumentOutOfRangeException>(() => original.WithInitialRefreshProfile((WatchMonitorProfile)99, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => original.WithInitialRefreshProfile(WatchMonitorProfile.Normal, -1));
    }

    [Fact]
    public async Task DisabledDeliveryStillRecoversAndPollsWithoutReadingProjectingReservingOrSendingPendingWork()
    {
        await using var fixture = new InteractiveRuntimeFixture(new(deliveryEnabled: false));
        fixture.Store.Watches = [MonitorFixture.Watch([MonitorFixture.Key()], initialNoticeEnabled: false)];
        fixture.Recovery.Read = _ => Task.FromResult(System.Collections.Immutable.ImmutableArray.Create(
            new UnresolvedDeliveryAttempt(BatchFixture.AttemptId, BatchFixture.BatchId, BatchFixture.Key(), MonitorFixture.Now)));
        await fixture.Session.StartAsync();
        await MonitorFixture.EventuallyAsync(() => !fixture.Store.CommitCommands.IsEmpty);
        Assert.Equal(1, fixture.Recovery.Reads);
        var recovery = Assert.Single(fixture.Batches.Results);
        Assert.Equal(BatchAttemptResultOutcome.InterruptedUnknown, recovery.Outcome);
        Assert.Equal(0, fixture.Work.Reads);
        Assert.Empty(fixture.Batches.Projects);
        Assert.Empty(fixture.Batches.Reservations);
        Assert.Empty(fixture.Channel.Envelopes);
        Assert.Contains(fixture.Source.Calls, call => call.Purpose == SnuWatcher.Application.Sources.SourceRequestPurpose.Watch);
        Assert.Null(fixture.Session.Failure);
        Assert.True((await fixture.Session.StopAsync()).IsFinalized);
        Assert.Single(fixture.Batches.Results);
    }

    [Fact]
    public async Task SessionForwardsRevisionedProfileChangesAndReportsActualAppliedSnapshotWithoutSourceWork()
    {
        var options = new RuntimeSessionOptions().WithInitialRefreshProfile(WatchMonitorProfile.LowActivity, 3);
        await using var fixture = new InteractiveRuntimeFixture(options);
        Assert.Equal(WatchMonitorProfile.LowActivity, fixture.Session.AppliedRefreshProfile.Profile);
        Assert.Equal(3, fixture.Session.AppliedRefreshProfile.PreferencesRevision);
        await fixture.Session.StartAsync();
        var applied = await fixture.Session.SetRefreshProfileAsync(WatchMonitorProfile.Faster, 5);
        Assert.Same(applied, fixture.Session.AppliedRefreshProfile);
        Assert.Equal(TimeSpan.FromSeconds(30), applied.TargetInterval);
        var old = await fixture.Session.SetRefreshProfileAsync(WatchMonitorProfile.Normal, 4);
        Assert.Same(applied, old);
        await Assert.ThrowsAsync<WatchMonitorProfileConflictException>(() =>
            fixture.Session.SetRefreshProfileAsync(WatchMonitorProfile.Normal, 5));
        Assert.Null(fixture.Session.Failure);
        Assert.Empty(fixture.Source.Calls);
        await fixture.Session.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Session.SetRefreshProfileAsync(WatchMonitorProfile.Normal, 6));
    }
}
