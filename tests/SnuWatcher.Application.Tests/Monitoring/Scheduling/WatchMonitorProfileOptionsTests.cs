using SnuWatcher.Application.Monitoring.Scheduling;
using Xunit;
using static SnuWatcher.Application.Tests.Monitoring.Scheduling.MonitorFixture;
using Session = SnuWatcher.Application.Tests.Monitoring.Scheduling.WatchMonitorLoopTests.Session;

namespace SnuWatcher.Application.Tests.Monitoring.Scheduling;

public sealed class WatchMonitorProfileOptionsTests
{
    [Fact]
    public void InitialProfileRevisionIsVisibleBeforeTheActorStarts()
    {
        var clock = new SplitMonitorClock();
        var store = new FakeMonitorStore();
        var options = new WatchMonitorOptions(WatchMonitorProfile.LowActivity, initialPreferencesRevision: 42);
        var loop = new WatchMonitorLoop(store, new FakeMonitorSource(store, clock), new FakeMonitorPolicy(), clock, options);

        Assert.Equal(0, new WatchMonitorOptions().InitialPreferencesRevision);
        Assert.Same(loop.Snapshot.AppliedRefreshProfile, loop.AppliedRefreshProfile);
        Assert.Equal(WatchMonitorProfile.LowActivity, loop.AppliedRefreshProfile.Profile);
        Assert.Equal(42, loop.AppliedRefreshProfile.PreferencesRevision);
        Assert.Equal(TimeSpan.FromSeconds(120), loop.AppliedRefreshProfile.TargetInterval);
    }

    [Fact]
    public void CloningTheProfilePreservesEveryOtherResourceBound()
    {
        var original = new WatchMonitorOptions(WatchMonitorProfile.Faster, 2, 317, 7, 9,
            TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(419), TimeSpan.FromSeconds(17), initialPreferencesRevision: 8);
        var updated = original.WithProfile(WatchMonitorProfile.LowActivity, 19);

        Assert.NotSame(original, updated);
        Assert.Equal(WatchMonitorProfile.Faster, original.Profile);
        Assert.Equal(8, original.InitialPreferencesRevision);
        Assert.Equal(WatchMonitorProfile.LowActivity, updated.Profile);
        Assert.Equal(19, updated.InitialPreferencesRevision);
        Assert.Equal(original.MaxConcurrentQueries, updated.MaxConcurrentQueries);
        Assert.Equal(original.MaxUniqueSections, updated.MaxUniqueSections);
        Assert.Equal(original.MaxCommitAttempts, updated.MaxCommitAttempts);
        Assert.Equal(original.JitterPercent, updated.JitterPercent);
        Assert.Equal(original.RecoveryStagger, updated.RecoveryStagger);
        Assert.Equal(original.CatalogRefreshInterval, updated.CatalogRefreshInterval);
        Assert.Equal(original.CommitRetryDelay, updated.CommitRetryDelay);
    }

    [Fact]
    public void UnsupportedInitialProfileOrRevisionIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatchMonitorOptions(initialPreferencesRevision: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatchMonitorOptions().WithProfile((WatchMonitorProfile)99, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatchMonitorOptions().WithProfile(WatchMonitorProfile.Faster, -1));
    }

    [Fact]
    public async Task HigherRevisionAppliesAndOlderIntentCannotReplaceIt()
    {
        await using var session = new Session(new(), new());
        var applied = await session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.LowActivity, 9, CancellationToken.None);
        var stale = await session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.Faster, 3, CancellationToken.None);
        var replay = await session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.LowActivity, 9, CancellationToken.None);

        Assert.Same(applied, stale);
        Assert.Same(applied, replay);
        Assert.Same(applied, session.Loop.Snapshot.AppliedRefreshProfile);
        Assert.Same(applied, session.Loop.AppliedRefreshProfile);
        Assert.Equal(9, applied.PreferencesRevision);
        Assert.Empty(session.Source.CatalogCalls);
    }

    [Fact]
    public async Task SameRevisionCannotIdentifyAnotherProfile()
    {
        await using var session = new Session(new(), new());
        await session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.LowActivity, 9, CancellationToken.None);
        var failure = await Assert.ThrowsAsync<WatchMonitorProfileConflictException>(() =>
            session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.Faster, 9, CancellationToken.None));

        Assert.Equal("monitor_profile_revision_conflict", failure.Code);
        Assert.Equal(WatchMonitorProfile.LowActivity, session.Loop.AppliedRefreshProfile.Profile);
    }

    [Fact]
    public async Task InvalidOrPreCanceledCommandsDoNotChangeAppliedState()
    {
        await using var session = new Session(new(), new());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            session.Loop.SetRefreshProfileAsync((WatchMonitorProfile)99, 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.Faster, -1, CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.Faster, 1, new CancellationToken(true)));

        Assert.Equal(0, session.Loop.AppliedRefreshProfile.PreferencesRevision);
        Assert.Equal(WatchMonitorProfile.Normal, session.Loop.AppliedRefreshProfile.Profile);
    }

    [Fact]
    public async Task CommandsAreRejectedBeforeStartAndAfterTheSingleLifetimeStops()
    {
        var clock = new SplitMonitorClock();
        var store = new FakeMonitorStore();
        var loop = new WatchMonitorLoop(store, new FakeMonitorSource(store, clock), new FakeMonitorPolicy(), clock, new());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            loop.SetRefreshProfileAsync(WatchMonitorProfile.Faster, 1, CancellationToken.None));
        using var stop = new CancellationTokenSource();
        var running = loop.RunAsync(stop.Token);
        stop.Cancel();
        await running;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            loop.SetRefreshProfileAsync(WatchMonitorProfile.Faster, 1, CancellationToken.None));
    }
}
