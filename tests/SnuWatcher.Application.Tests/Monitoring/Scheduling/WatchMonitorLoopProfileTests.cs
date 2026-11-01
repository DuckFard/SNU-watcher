using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Sources;
using Xunit;
using static SnuWatcher.Application.Tests.Monitoring.Scheduling.MonitorFixture;
using Session = SnuWatcher.Application.Tests.Monitoring.Scheduling.WatchMonitorLoopTests.Session;

namespace SnuWatcher.Application.Tests.Monitoring.Scheduling;

public sealed class WatchMonitorLoopProfileTests
{
    [Fact]
    public async Task FasterUsesTheOriginalStartAndCannotUndercutTheFloor()
    {
        var clock = new SplitMonitorClock();
        var store = new FakeMonitorStore([Watch([Key()])]);
        using var releaseTimer = new ManualResetEventSlim();
        await using var session = new Session(store, clock);
        await WaitingAsync(session);
        clock.Advance(TimeSpan.FromSeconds(5));
        var creatingTimer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var intercepted = 0;
        clock.BeforeTimerCreated = _ =>
        {
            if (session.Loop.AppliedRefreshProfile.PreferencesRevision != 1 || Interlocked.CompareExchange(ref intercepted, 1, 0) != 0) return;
            creatingTimer.TrySetResult();
            if (!releaseTimer.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The test did not release timer creation.");
        };
        try
        {
            await session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.Faster, 1, CancellationToken.None);
            Assert.Equal(TimeSpan.FromSeconds(25), session.Loop.Snapshot.Queries.Single().NextCheckIn);
            await creatingTimer.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Hold the actor after it chooses the delay but before a timer can anchor it.
            clock.Advance(TimeSpan.FromSeconds(24));
            Assert.Single(session.Source.SearchCalls);
            clock.Advance(TimeSpan.FromSeconds(1));
            releaseTimer.Set();
            await EventuallyAsync(() => session.Source.SearchCalls.Length == 2);
            Assert.Equal(TimeSpan.FromSeconds(30), clock.GetElapsedTime(session.Source.SearchCalls[0].StartedTimestamp,
                session.Source.SearchCalls[1].StartedTimestamp));
        }
        finally { clock.BeforeTimerCreated = null; releaseTimer.Set(); }
    }

    [Fact]
    public async Task PartialTimerSetupDelayArmsOnlyTheRemainingOriginalDeadline()
    {
        var clock = new SplitMonitorClock();
        using var releaseTimer = new ManualResetEventSlim();
        await using var session = new Session(new([Watch([Key()])]), clock);
        await WaitingAsync(session);
        clock.Advance(TimeSpan.FromSeconds(5));
        var creatingTimer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armedTimer = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var intercepted = 0;
        clock.BeforeTimerCreated = _ =>
        {
            if (session.Loop.AppliedRefreshProfile.PreferencesRevision != 1 || Interlocked.CompareExchange(ref intercepted, 1, 0) != 0) return;
            creatingTimer.TrySetResult();
            if (!releaseTimer.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The test did not release timer creation.");
        };
        clock.AfterTimerArmed = delay =>
        {
            if (session.Loop.AppliedRefreshProfile.PreferencesRevision == 1) armedTimer.TrySetResult(delay);
        };
        try
        {
            await session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.Faster, 1, CancellationToken.None);
            await creatingTimer.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(24));
            Assert.Single(session.Source.SearchCalls);
            releaseTimer.Set();
            Assert.Equal(TimeSpan.FromSeconds(1), await armedTimer.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Single(session.Source.SearchCalls);
            clock.Advance(TimeSpan.FromSeconds(1));
            await EventuallyAsync(() => session.Source.SearchCalls.Length == 2);
            Assert.Equal(TimeSpan.FromSeconds(30), clock.GetElapsedTime(session.Source.SearchCalls[0].StartedTimestamp,
                session.Source.SearchCalls[1].StartedTimestamp));
        }
        finally { clock.BeforeTimerCreated = null; clock.AfterTimerArmed = null; releaseTimer.Set(); }
    }

    [Fact]
    public async Task ProfileWakeDuringTimerCreationDrainsTheOldWaitAndPreservesTheNewIntent()
    {
        var clock = new SplitMonitorClock();
        using var releaseTimer = new ManualResetEventSlim();
        await using var session = new Session(new([Watch([Key()])]), clock);
        await WaitingAsync(session);
        clock.Advance(TimeSpan.FromSeconds(5));
        var creatingTimer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextTimer = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var intercepted = 0;
        clock.BeforeTimerCreated = _ =>
        {
            if (session.Loop.AppliedRefreshProfile.PreferencesRevision != 1 || Interlocked.CompareExchange(ref intercepted, 1, 0) != 0) return;
            creatingTimer.TrySetResult();
            if (!releaseTimer.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The test did not release timer creation.");
        };
        clock.AfterTimerArmed = delay =>
        {
            if (session.Loop.AppliedRefreshProfile.PreferencesRevision == 2) nextTimer.TrySetResult(delay);
        };
        try
        {
            await session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.Faster, 1, CancellationToken.None);
            await creatingTimer.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var changed = session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.LowActivity, 2, CancellationToken.None);
            Assert.False(changed.IsCompleted);
            releaseTimer.Set();
            Assert.Equal(2, (await changed).PreferencesRevision);
            Assert.Equal(TimeSpan.FromSeconds(115), await nextTimer.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, clock.UndisposedTimerCount);
            Assert.Single(session.Source.SearchCalls);
            session.Stop();
            await session.Running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, clock.UndisposedTimerCount);
        }
        finally { clock.BeforeTimerCreated = null; clock.AfterTimerArmed = null; releaseTimer.Set(); }
    }

    [Fact]
    public async Task StopDuringTimerCreationDrainsTheActualTimerAndReaderWithoutAnotherQuery()
    {
        var clock = new SplitMonitorClock();
        using var releaseTimer = new ManualResetEventSlim();
        await using var session = new Session(new([Watch([Key()])]), clock);
        await WaitingAsync(session);
        clock.Advance(TimeSpan.FromSeconds(5));
        var creatingTimer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var intercepted = 0;
        clock.BeforeTimerCreated = _ =>
        {
            if (session.Loop.AppliedRefreshProfile.PreferencesRevision != 1 || Interlocked.CompareExchange(ref intercepted, 1, 0) != 0) return;
            creatingTimer.TrySetResult();
            if (!releaseTimer.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The test did not release timer creation.");
        };
        try
        {
            await session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.Faster, 1, CancellationToken.None);
            await creatingTimer.Task.WaitAsync(TimeSpan.FromSeconds(5));
            session.Stop();
            Assert.False(session.Running.IsCompleted);
            releaseTimer.Set();
            await session.Running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, clock.UndisposedTimerCount);
            Assert.Single(session.Source.SearchCalls);
        }
        finally { clock.BeforeTimerCreated = null; releaseTimer.Set(); }
    }

    [Fact]
    public async Task SlowerThenFasterReplacesOnlyTheNominalDeadlineAndCreatesNoRunDebt()
    {
        var clock = new SplitMonitorClock();
        await using var session = new Session(new([Watch([Key()])]), clock);
        await WaitingAsync(session);
        clock.Advance(TimeSpan.FromSeconds(20));
        await session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.LowActivity, 1, CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(100), session.Loop.Snapshot.Queries.Single().NextCheckIn);
        clock.Advance(TimeSpan.FromSeconds(15));
        await session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.Faster, 2, CancellationToken.None);
        await EventuallyAsync(() => session.Source.SearchCalls.Length == 2);
        await EventuallyAsync(() => session.Loop.Snapshot.Queries.Any(query => query.State == WatchMonitorWorkState.Waiting
            && query.LastStartedAtUtc == session.Source.SearchCalls[1].StartedAtUtc));
        await Task.Delay(20);
        Assert.Equal(2, session.Source.SearchCalls.Length);
        Assert.Equal(TimeSpan.FromSeconds(30), session.Loop.Snapshot.Queries.Single().NextCheckIn);
    }

    [Fact]
    public async Task NewRevisionOfTheSameProfileDoesNotResetTimingOrJitter()
    {
        var clock = new SplitMonitorClock();
        await using var session = new Session(new([Watch([Key()])]), clock, options: new(jitterPercent: 10));
        await WaitingAsync(session);
        var before = session.Loop.Snapshot.Queries.Single().NextCheckIn!.Value;
        clock.Advance(TimeSpan.FromSeconds(7));
        var result = await session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.Normal, 2, CancellationToken.None);
        Assert.Equal(2, result.PreferencesRevision);
        Assert.Equal(before - TimeSpan.FromSeconds(7), session.Loop.Snapshot.Queries.Single().NextCheckIn);
        Assert.Single(session.Source.SearchCalls);
    }

    [Fact]
    public async Task SlowerProfileDelaysAnExistingErrorFenceAndTheFollowingFailedCompletion()
    {
        var clock = new SplitMonitorClock();
        await using var session = new Session(new([Watch([Key()])]), clock, configure: source => source.SearchHandler = (_, _) =>
            Task.FromResult(Failure(clock)));
        await EventuallyAsync(() => session.Loop.Snapshot.Queries.Any(query => query.FailureCount == 1));
        clock.Advance(TimeSpan.FromSeconds(10));
        await session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.LowActivity, 1, CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(110), session.Loop.Snapshot.Queries.Single().NextCheckIn);
        clock.Advance(TimeSpan.FromSeconds(109));
        await Task.Delay(20);
        Assert.Single(session.Source.SearchCalls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => session.Loop.Snapshot.Queries.Single().FailureCount == 2);
        Assert.Equal(TimeSpan.FromSeconds(120), session.Loop.Snapshot.Queries.Single().NextCheckIn);
    }

    [Fact]
    public async Task FasterProfileCannotEraseTheIndependentLongerErrorFence()
    {
        var clock = new SplitMonitorClock();
        await using var session = new Session(new([Watch([Key()])]), clock, configure: source => source.SearchHandler = (_, _) =>
            Task.FromResult(Failure(clock)));
        await EventuallyAsync(() => session.Loop.Snapshot.Queries.Any(query => query.FailureCount == 1));
        clock.Advance(TimeSpan.FromSeconds(60));
        await EventuallyAsync(() => session.Loop.Snapshot.Queries.Single().FailureCount == 2);
        clock.Advance(TimeSpan.FromSeconds(10));
        await session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.Faster, 1, CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(50), session.Loop.Snapshot.Queries.Single().NextCheckIn);
        clock.Advance(TimeSpan.FromSeconds(49));
        await Task.Delay(20);
        Assert.Equal(2, session.Source.SearchCalls.Length);
        clock.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => session.Source.SearchCalls.Length == 3);
    }

    private static Task WaitingAsync(Session session) => EventuallyAsync(() =>
        session.Loop.Snapshot.Queries.Any(query => query.State == WatchMonitorWorkState.Waiting));
    private static SourceRead<CourseSearchData> Failure(TimeProvider clock, TimeSpan? retryAfter = null) => new(
        SourceReadStatus.Failed, null, [new SourceProblem(SourceProblemKind.Unavailable, "source_unavailable", retryAfter)], clock.GetUtcNow());
}
