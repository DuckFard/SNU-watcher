using System.Collections.Immutable;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Sources;
using Xunit;
using static SnuWatcher.Application.Tests.Monitoring.Scheduling.MonitorFixture;
using Session = SnuWatcher.Application.Tests.Monitoring.Scheduling.WatchMonitorLoopTests.Session;

namespace SnuWatcher.Application.Tests.Monitoring.Scheduling;

public sealed class WatchMonitorProfileOwnershipTests
{
    [Fact]
    public async Task PendingCommandsCoalesceAndSamePendingRevisionConflictsExplicitly()
    {
        var release = NewList();
        var store = new FakeMonitorStore { ListHandler = token => release.Task.WaitAsync(token) };
        await using var session = new Session(store, new());
        var original = session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.Faster, 1, CancellationToken.None);
        var latest = session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.LowActivity, 3, CancellationToken.None);
        var stale = session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.Normal, 2, CancellationToken.None);
        await Assert.ThrowsAsync<WatchMonitorProfileConflictException>(() =>
            session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.Faster, 3, CancellationToken.None));
        Assert.False(original.IsCompleted);
        release.SetResult([]);

        var applied = await latest.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(applied, await original.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(applied, await stale.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, applied.PreferencesRevision);
        Assert.Equal(WatchMonitorProfile.LowActivity, applied.Profile);
        Assert.Empty(session.Source.CatalogCalls);
    }

    [Fact]
    public async Task CancelingAQueuedWaiterCannotCancelTheAcceptedApplication()
    {
        var release = NewList();
        var store = new FakeMonitorStore { ListHandler = token => release.Task.WaitAsync(token) };
        await using var session = new Session(store, new());
        using var canceled = new CancellationTokenSource();
        var detached = session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.LowActivity, 4, canceled.Token);
        var joined = session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.LowActivity, 4, CancellationToken.None);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => detached);
        release.SetResult([]);
        Assert.Equal(4, (await joined.WaitAsync(TimeSpan.FromSeconds(5))).PreferencesRevision);
        Assert.Equal(WatchMonitorProfile.LowActivity, session.Loop.AppliedRefreshProfile.Profile);
    }

    [Fact]
    public async Task ANewerPendingRevisionCannotHideConflictsWithClaimedOrAppliedIntent()
    {
        var release = NewList();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeMonitorStore();
        await using var session = new Session(store, new());
        await EventuallyAsync(() => store.ReadCount > 0);
        store.ListHandler = token => { entered.TrySetResult(); return release.Task.WaitAsync(token); };
        // The actor claims controls before its read, then publishes/acknowledges after that coherent iteration.
        var claimed = session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.Faster, 1, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.LowActivity, 3, CancellationToken.None);
        await Assert.ThrowsAsync<WatchMonitorProfileConflictException>(() =>
            session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.Normal, 1, CancellationToken.None));
        await Assert.ThrowsAsync<WatchMonitorProfileConflictException>(() =>
            session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.LowActivity, 0, CancellationToken.None));
        Assert.Equal(0, session.Loop.AppliedRefreshProfile.PreferencesRevision);
        release.SetResult([]);
        Assert.Equal(1, (await claimed.WaitAsync(TimeSpan.FromSeconds(5))).PreferencesRevision);
        Assert.Equal(3, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).PreferencesRevision);
    }

    [Theory]
    [InlineData(WatchMonitorProfile.Faster, 30, false)]
    [InlineData(WatchMonitorProfile.LowActivity, 120, false)]
    [InlineData(WatchMonitorProfile.Faster, 30, true)]
    [InlineData(WatchMonitorProfile.LowActivity, 120, true)]
    public async Task ChangingProfileKeepsTheActualInFlightTokenAndUsesItForCompletion(
        WatchMonitorProfile profile, int seconds, bool failed)
    {
        var clock = new SplitMonitorClock();
        var read = NewRead();
        var store = new FakeMonitorStore([Watch([Key()])]);
        CancellationToken sourceToken = default;
        await using var session = new Session(store, clock, configure: source => source.SearchHandler = (_, token) =>
        { sourceToken = token; return read.Task; });
        var cleanup = Rows(new(Key().Year, "21", Key().CourseCode), [Key()], clock.GetUtcNow());
        try
        {
            await EventuallyAsync(() => session.Source.SearchCalls.Length == 1);
            var token = Assert.Single(store.Issues).Token;
            var manual = session.Loop.RefreshAsync(Key(), CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(10));
            await session.Loop.SetRefreshProfileAsync(profile, 1, CancellationToken.None);
            Assert.False(sourceToken.IsCancellationRequested);
            Assert.Single(store.Issues);
            Assert.Single(session.Source.SearchCalls);
            read.SetResult(failed ? new(SourceReadStatus.Failed, null,
                [new SourceProblem(SourceProblemKind.Unavailable, "source_unavailable")], clock.GetUtcNow())
                : Rows(session.Source.SearchCalls[0].Query, [Key()], clock.GetUtcNow()));
            Assert.Equal(WatchRefreshDisposition.Completed, (await manual.WaitAsync(TimeSpan.FromSeconds(5))).Disposition);
            await EventuallyAsync(() => session.Loop.Snapshot.Queries.Single().State == WatchMonitorWorkState.Waiting);
            Assert.Equal(token, Assert.Single(store.CommitCommands).Completion.Token);
            Assert.Equal(failed ? 1 : 0, session.Loop.Snapshot.Queries.Single().FailureCount);
            Assert.Equal(TimeSpan.FromSeconds(seconds - 10), session.Loop.Snapshot.Queries.Single().NextCheckIn);
        }
        finally { read.TrySetResult(cleanup); }
    }

    [Fact]
    public async Task ProfileApplicationDoesNotRescheduleOrRefetchARetainedCommit()
    {
        var clock = new SplitMonitorClock();
        var store = new FakeMonitorStore([Watch([Key()])]) { ConflictWritesRemaining = 3 };
        await using var session = new Session(store, clock);
        await EventuallyAsync(() => session.Loop.Snapshot.Queries.Any(query => query.State == WatchMonitorWorkState.CommitDelayed));
        var original = store.CommitCommands[0].Completion;
        clock.Advance(TimeSpan.FromSeconds(1));
        await session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.LowActivity, 1, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(3));
        await Task.Delay(20);
        Assert.Equal(3, store.CommitCommands.Length);
        clock.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => store.SectionSnapshots.Count == 1
            && session.Loop.Snapshot.Queries.Single().State == WatchMonitorWorkState.Waiting);
        Assert.Single(session.Source.SearchCalls);
        Assert.All(store.CommitCommands, command => Assert.Equal(original, command.Completion));
        Assert.Equal(TimeSpan.FromSeconds(115), session.Loop.Snapshot.Queries.Single().NextCheckIn);
    }

    [Fact]
    public async Task StopMakesPendingApplicationTerminalBeforeAnUncooperativeReadDrains()
    {
        var clock = new SplitMonitorClock();
        var read = NewRead();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = NewList();
        var store = new FakeMonitorStore([Watch([Key()])]);
        await using var session = new Session(store, clock, configure: source => source.SearchHandler = (_, _) => read.Task);
        var cleanup = Rows(new(Key().Year, "21", Key().CourseCode), [Key()], clock.GetUtcNow());
        try
        {
            await EventuallyAsync(() => session.Source.SearchCalls.Length == 1);
            store.ListHandler = token => { entered.TrySetResult(); return block.Task.WaitAsync(token); };
            session.Loop.Wake();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.Faster, 1, CancellationToken.None);
            session.Stop();
            await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(session.Running.IsCompleted);
        }
        finally { read.TrySetResult(cleanup); }
        await session.Running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(store.CommitCommands);
    }

    [Fact]
    public async Task AFailedActorReadCannotLeaveAnAcceptedProfileWaiterHanging()
    {
        var release = NewList();
        var store = new FakeMonitorStore { ListHandler = token => release.Task.WaitAsync(token) };
        await using var session = new Session(store, new());
        var pending = session.Loop.SetRefreshProfileAsync(WatchMonitorProfile.Faster, 1, CancellationToken.None);
        release.SetException(new IOException("private database path"));
        var failure = await Assert.ThrowsAsync<WatchMonitorException>(() => session.Running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("monitor_dependency_failed", failure.Code);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, session.Loop.AppliedRefreshProfile.PreferencesRevision);
    }

    private static TaskCompletionSource<ImmutableArray<SubscriptionSnapshot>> NewList() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<SourceRead<CourseSearchData>> NewRead() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
