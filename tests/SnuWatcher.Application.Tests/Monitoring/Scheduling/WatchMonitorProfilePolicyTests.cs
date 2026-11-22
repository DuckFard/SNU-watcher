using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Sources;
using Xunit;
using static SnuWatcher.Application.Tests.Monitoring.Scheduling.MonitorFixture;
using Session = SnuWatcher.Application.Tests.Monitoring.Scheduling.WatchMonitorLoopTests.Session;

namespace SnuWatcher.Application.Tests.Monitoring.Scheduling;

public sealed class WatchMonitorProfilePolicyTests
{
    [Theory]
    [InlineData(WatchMonitorProfile.Normal, 60)]
    [InlineData(WatchMonitorProfile.LowActivity, 120)]
    [InlineData(WatchMonitorProfile.Faster, 30)]
    public async Task AutomaticFailureRetriesHonorTheProfileAndTheIndependentErrorFence(
        WatchMonitorProfile profile, int seconds)
    {
        var clock = new SplitMonitorClock();
        await using var session = new Session(new([Watch([Key()])]), clock, options: new(profile, jitterPercent: 0),
            configure: source => source.SearchHandler = (_, _) => Task.FromResult(Failure(clock)));
        await EventuallyAsync(() => session.Loop.Snapshot.Queries.Any(query => query.FailureCount == 1));
        Assert.Equal(TimeSpan.FromSeconds(seconds), session.Loop.Snapshot.Queries.Single().NextCheckIn);
        clock.Advance(TimeSpan.FromSeconds(seconds - 1));
        await Task.Delay(20);
        Assert.Single(session.Source.SearchCalls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => session.Loop.Snapshot.Queries.Single().FailureCount == 2);
        Assert.Equal(TimeSpan.FromSeconds(Math.Max(60, seconds)), session.Loop.Snapshot.Queries.Single().NextCheckIn);
    }

    [Fact]
    public async Task ProfileChangesRetainLongRetryAfterAcrossUtcCorrections()
    {
        var clock = new SplitMonitorClock();
        await using var session = new Session(new([Watch([Key()])]), clock, configure: source => source.SearchHandler = (_, _) =>
            Task.FromResult(Failure(clock, TimeSpan.FromHours(3))));
        await EventuallyAsync(() => session.Loop.Snapshot.Queries.Any(query => query.FailureCount == 1));
        await session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.LowActivity, 1, CancellationToken.None);
        await session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.Faster, 2, CancellationToken.None);
        clock.ShiftUtc(TimeSpan.FromDays(3));
        clock.Advance(TimeSpan.FromHours(3) - TimeSpan.FromSeconds(1));
        await Task.Delay(20);
        Assert.Single(session.Source.SearchCalls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => session.Source.SearchCalls.Length == 2);
    }

    [Fact]
    public async Task ProfileApplicationCannotClearThePersistedCooldownOrAccessRestriction()
    {
        var clock = new SplitMonitorClock();
        var policy = new FakeMonitorPolicy { Snapshot = new(SourcePolicyIds.SnuSugang, 1,
            SourceRequestProgress.Empty, cooldown: new SourceCooldown(clock.GetUtcNow(), TimeSpan.FromMinutes(2))) };
        await using var session = new Session(new([Watch([Key()])]), clock, policy: policy);
        await EventuallyAsync(() => session.Loop.Snapshot.ReasonCode == "source_cooldown");
        await session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.Faster, 1, CancellationToken.None);
        clock.ShiftUtc(TimeSpan.FromDays(1));
        clock.Advance(TimeSpan.FromSeconds(119));
        await Task.Delay(20);
        Assert.Empty(session.Source.CatalogCalls);
        policy.Snapshot = new(SourcePolicyIds.SnuSugang, 2,
            new SourceRequestProgress(1, clock.GetUtcNow(), 1, SourceRequestOutcome.AccessRestricted, clock.GetUtcNow(), 0, null),
            new SourceAccessPause(1, clock.GetUtcNow(), "access_restricted"));
        session.Loop.Wake();
        await EventuallyAsync(() => session.Loop.Snapshot.ReasonCode == "source_access_restricted");
        await session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.LowActivity, 2, CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1));
        await Task.Delay(20);
        Assert.Empty(session.Source.CatalogCalls);
        Assert.Equal(2, policy.Snapshot.Revision);
        Assert.Equal("access_restricted", policy.Snapshot.AccessPause!.ReasonCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfflineOrSuspendedProfileChangesSurviveRecoveryWithoutErasingTheTarget(bool suspended)
    {
        var clock = new SplitMonitorClock();
        await using var session = new Session(new([Watch([Key()])]), clock);
        await EventuallyAsync(() => session.Loop.Snapshot.Queries.Any(query => query.State == WatchMonitorWorkState.Waiting));
        if (suspended) session.Loop.SetSuspended(true); else session.Loop.SetOnline(false);
        await EventuallyAsync(() => session.Loop.Snapshot.ReasonCode == (suspended ? "suspended" : "offline"));
        clock.Advance(TimeSpan.FromSeconds(10));
        await session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.LowActivity, 1, CancellationToken.None);
        if (suspended) session.Loop.SetSuspended(false); else session.Loop.SetOnline(true);
        clock.Advance(TimeSpan.FromSeconds(109));
        await Task.Delay(20);
        Assert.Single(session.Source.SearchCalls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => session.Source.SearchCalls.Length == 2);
    }

    [Fact]
    public async Task ExplicitManualRefreshKeepsItsFloorAndErrorFenceRatherThanTheAutomaticTarget()
    {
        var clock = new SplitMonitorClock();
        await using var session = new Session(new([Watch([Key()])]), clock, options: new(WatchMonitorProfile.LowActivity, jitterPercent: 0),
            configure: source => source.SearchHandler = (_, _) => Task.FromResult(Failure(clock)));
        await EventuallyAsync(() => session.Loop.Snapshot.Queries.Any(query => query.FailureCount == 1));
        clock.Advance(TimeSpan.FromSeconds(1));
        var manual = session.Loop.RefreshAsync(Key(), CancellationToken.None);
        await session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.Normal, 1, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(28));
        await Task.Delay(20);
        Assert.Single(session.Source.SearchCalls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await manual.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, session.Source.SearchCalls.Length);
    }

    private static SourceRead<CourseSearchData> Failure(TimeProvider clock, TimeSpan? retryAfter = null) => new(
        SourceReadStatus.Failed, null, [new SourceProblem(SourceProblemKind.Unavailable, "source_unavailable", retryAfter)], clock.GetUtcNow());
}
