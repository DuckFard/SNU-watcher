using System.Threading.Channels;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Sources;
using Xunit;
using static SnuWatcher.Application.Tests.Monitoring.Scheduling.MonitorFixture;
using Session = SnuWatcher.Application.Tests.Monitoring.Scheduling.WatchMonitorLoopTests.Session;

namespace SnuWatcher.Application.Tests.Monitoring.Scheduling;

public sealed class WatchMonitorLoopPolicyTests
{
    [Fact]
    public async Task OverrunningCatalogRefreshCannotStarveAnOlderDueWatchInTheOnlySlot()
    {
        var clock = new SplitMonitorClock();
        var store = new FakeMonitorStore([Watch([Key()])]);
        var catalogRead = new TaskCompletionSource<SourceRead<CourseCatalogData>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterCatalogRead = new TaskCompletionSource<SourceRead<CourseCatalogData>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await using var session = new Session(store, clock, options: new(maxConcurrentQueries: 1, jitterPercent: 0,
            catalogRefreshInterval: TimeSpan.FromSeconds(30)), configure: source => source.CatalogHandler = (query, token) =>
            Interlocked.Increment(ref count) switch
            {
                1 => Task.FromResult(Catalog(query, clock.GetUtcNow())),
                2 => catalogRead.Task.WaitAsync(token),
                _ => laterCatalogRead.Task.WaitAsync(token),
            });
        await EventuallyAsync(() => session.Source.SearchCalls.Length == 1);
        clock.Advance(TimeSpan.FromSeconds(30));
        await EventuallyAsync(() => session.Source.CatalogCalls.Length == 2);
        clock.Advance(TimeSpan.FromSeconds(40));
        catalogRead.SetResult(Catalog(new(2026), clock.GetUtcNow()));
        await EventuallyAsync(() => session.Source.SearchCalls.Length >= 2);
    }

    [Fact]
    public async Task LastStartedUtcIsCapturedBeforeASynchronouslyCompletedPipelineChangesTheClock()
    {
        var clock = new SplitMonitorClock();
        var started = clock.GetUtcNow();
        var store = new FakeMonitorStore([Watch([Key()])]);
        await using var session = new Session(store, clock, configure: source => source.SearchHandler = (query, _) =>
        {
            clock.ShiftUtc(TimeSpan.FromHours(1));
            return Task.FromResult(Rows(query, [Key()], clock.GetUtcNow()));
        });
        await EventuallyAsync(() => session.Loop.Snapshot.Queries.Any(query => query.State == WatchMonitorWorkState.Waiting));
        Assert.Equal(started, session.Loop.Snapshot.Queries.Single().LastStartedAtUtc);
    }

    [Fact]
    public async Task ThrowingCancellationCallbackCannotSkipTheActualOwnedTaskDrain()
    {
        var clock = new SplitMonitorClock();
        var store = new FakeMonitorStore([Watch([Key()])]);
        var read = NewRead();
        await using var session = new Session(store, clock, configure: source => source.SearchHandler = (_, token) =>
        {
            token.Register(() => throw new InvalidOperationException("private callback error"));
            return read.Task;
        });
        await EventuallyAsync(() => session.Source.SearchCalls.Length == 1);
        try
        {
            Assert.Null(Record.Exception(() => session.Loop.SetOnline(false)));
            await Task.Delay(20);
            Assert.False(session.Running.IsCompleted);
            read.SetResult(Rows(session.Source.SearchCalls[0].Query, [Key()], clock.GetUtcNow()));
            var error = await Assert.ThrowsAsync<WatchMonitorException>(() => session.Running.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("monitor_cancellation_failed", error.Code);
            Assert.Empty(store.CommitCommands);
        }
        finally { read.TrySetResult(Rows(session.Source.SearchCalls[0].Query, [Key()], clock.GetUtcNow())); }
    }

    [Fact]
    public async Task FourLogicalQueriesAreBoundedAndAnEarlierCompletionAdmitsTheNextWithoutOverlap()
    {
        var clock = new SplitMonitorClock();
        var keys = Enumerable.Range(0, 5).Select(index => Key(course: $"C{index}")).ToArray();
        var store = new FakeMonitorStore(keys.Select(key => Watch([key])));
        var reads = keys.ToDictionary(key => key.CourseCode, _ => NewRead());
        await using var session = new Session(store, clock, configure: source => source.SearchHandler =
            (query, token) => reads[query.CourseCode!].Task.WaitAsync(token));
        await EventuallyAsync(() => session.Source.SearchCalls.Length == 1);
        clock.Advance(TimeSpan.FromSeconds(6));
        await EventuallyAsync(() => session.Source.SearchCalls.Length == 4);
        clock.Advance(TimeSpan.FromSeconds(2));
        await Task.Delay(20);
        Assert.Equal(4, session.Source.SearchCalls.Length);
        var queued = session.Loop.Snapshot.Queries.Single(query => query.Query.CourseCode == "C4");
        Assert.Equal("logical_queue", queued.ReasonCode);
        Assert.Null(queued.NextCheckIn);
        reads["C0"].SetResult(Rows(session.Source.SearchCalls[0].Query, [keys[0]], clock.GetUtcNow()));
        await EventuallyAsync(() => session.Source.SearchCalls.Length == 5);
        Assert.Equal(4, session.Source.MaxActive);
    }

    [Theory]
    [InlineData(SourceProblemKind.Unavailable, 60, 60)]
    [InlineData(SourceProblemKind.ParseChanged, 120, 240)]
    public async Task ConsecutiveFailuresUseDistinctStartBasedBackoffAndSuccessResetsIt(
        SourceProblemKind kind, int firstSeconds, int secondSeconds)
    {
        var clock = new SplitMonitorClock();
        var store = new FakeMonitorStore([Watch([Key()])]);
        var count = 0;
        await using var session = new Session(store, clock, configure: source => source.SearchHandler = (query, _) =>
            Task.FromResult(Interlocked.Increment(ref count) <= 2 ? Failure<CourseSearchData>(kind, clock) : Rows(query, [Key()], clock.GetUtcNow())));
        await EventuallyAsync(() => session.Loop.Snapshot.Queries.Any(query => query.FailureCount == 1));
        // M5: automatic retries retain the independent error fence and the applied Normal60 target.
        clock.Advance(TimeSpan.FromSeconds(firstSeconds - 1));
        await Task.Delay(20);
        Assert.Single(session.Source.SearchCalls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => session.Loop.Snapshot.Queries.Single().FailureCount == 2);
        clock.Advance(TimeSpan.FromSeconds(secondSeconds));
        await EventuallyAsync(() => session.Source.SearchCalls.Length == 3 && session.Loop.Snapshot.Queries.Single().FailureCount == 0);
        Assert.Equal(TimeSpan.FromSeconds(60), session.Loop.Snapshot.Queries.Single().NextCheckIn);
    }

    [Fact]
    public async Task LongRetryAfterOutlivesLocalBackoffAndUtcAdjustment()
    {
        var clock = new SplitMonitorClock();
        var store = new FakeMonitorStore([Watch([Key()])]);
        var count = 0;
        await using var session = new Session(store, clock, configure: source => source.SearchHandler = (query, _) =>
            Task.FromResult(Interlocked.Increment(ref count) == 1
                ? Failure<CourseSearchData>(SourceProblemKind.RateLimited, clock, TimeSpan.FromHours(3))
                : Rows(query, [Key()], clock.GetUtcNow())));
        await EventuallyAsync(() => session.Loop.Snapshot.Queries.Any(query => query.FailureCount == 1));
        clock.ShiftUtc(TimeSpan.FromDays(5));
        session.Loop.Wake();
        clock.Advance(TimeSpan.FromHours(3) - TimeSpan.FromSeconds(1));
        await Task.Delay(20);
        Assert.Single(session.Source.SearchCalls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => session.Source.SearchCalls.Length == 2);
    }

    [Theory]
    [InlineData(SourceProblemKind.Unavailable, 30)]
    [InlineData(SourceProblemKind.ParseChanged, 120)]
    public async Task FailedCatalogRecoversAtItsOwnBackoffWithoutUnfilteredSearch(SourceProblemKind kind, int seconds)
    {
        var clock = new SplitMonitorClock();
        var store = new FakeMonitorStore([Watch([Key()])]);
        var count = 0;
        await using var session = new Session(store, clock, configure: source => source.CatalogHandler = (query, _) =>
            Task.FromResult(Interlocked.Increment(ref count) == 1 ? Failure<CourseCatalogData>(kind, clock)
                : Catalog(query, clock.GetUtcNow())));
        await EventuallyAsync(() => session.Loop.Snapshot.Catalogs.Any(catalog => catalog.ReasonCode is not null));
        Assert.Empty(session.Source.SearchCalls);
        Assert.Equal("catalog_unavailable", Assert.Single(session.Loop.Snapshot.BlockedSections).ReasonCode);
        clock.Advance(TimeSpan.FromSeconds(seconds));
        await EventuallyAsync(() => session.Source.SearchCalls.Length == 1);
        Assert.Equal(2, session.Source.CatalogCalls.Length);
        Assert.All(session.Source.SearchCalls, call => Assert.Equal("F32.102", call.Query.CourseCode));
    }

    [Fact]
    public async Task MetadataCacheReceiptIsNeverRestampedAndInactiveYearsStopRefreshing()
    {
        var clock = new SplitMonitorClock();
        var watch = Watch([Key()]);
        var store = new FakeMonitorStore([watch]);
        var original = Catalog(new(2026), clock.GetUtcNow());
        await using var session = new Session(store, clock, configure: source => source.CatalogHandler = (_, _) => Task.FromResult(original));
        await EventuallyAsync(() => store.CommitCommands.Length == 1);
        clock.Advance(TimeSpan.FromMinutes(5));
        await EventuallyAsync(() => session.Source.CatalogCalls.Length == 2);
        Assert.Equal(original.CompletedAtUtc, session.Loop.Snapshot.Catalogs.Single().LastSuccessfulAtUtc);
        store.Watches = [];
        session.Loop.Wake();
        await EventuallyAsync(() => session.Loop.Snapshot.Queries.IsEmpty && session.Loop.Snapshot.Catalogs.IsEmpty);
        clock.Advance(TimeSpan.FromHours(1));
        await Task.Delay(20);
        Assert.Equal(2, session.Source.CatalogCalls.Length);
    }

    [Theory]
    [InlineData(WatchMonitorProfile.Faster, 30, 33)]
    [InlineData(WatchMonitorProfile.Normal, 54, 66)]
    [InlineData(WatchMonitorProfile.LowActivity, 108, 132)]
    public async Task JitterIsBoundedDeterministicAndNeverUndercutsTheStartFloor(WatchMonitorProfile profile, int minimum, int maximum)
    {
        var clock = new SplitMonitorClock();
        var store = new FakeMonitorStore([Watch([Key()])]);
        await using var first = new Session(store, clock, options: new(profile, jitterPercent: 10));
        await EventuallyAsync(() => first.Loop.Snapshot.Queries.Any(query => query.State == WatchMonitorWorkState.Waiting));
        var interval = first.Loop.Snapshot.Queries.Single().NextCheckIn!.Value;
        Assert.InRange(interval, TimeSpan.FromSeconds(minimum), TimeSpan.FromSeconds(maximum));
        await using var second = new Session(new([Watch([Key()])]), new(), options: new(profile, jitterPercent: 10));
        await EventuallyAsync(() => second.Loop.Snapshot.Queries.Any(query => query.State == WatchMonitorWorkState.Waiting));
        Assert.Equal(interval, second.Loop.Snapshot.Queries.Single().NextCheckIn);
    }

    [Fact]
    public async Task AnIdleManualRefreshWaitsForTheThirtySecondFloor()
    {
        var clock = new SplitMonitorClock();
        var store = new FakeMonitorStore([Watch([Key()])]);
        await using var session = new Session(store, clock);
        await EventuallyAsync(() => session.Loop.Snapshot.Queries.Any(query => query.State == WatchMonitorWorkState.Waiting));
        clock.Advance(TimeSpan.FromSeconds(1));
        var refresh = session.Loop.RefreshAsync(Key(), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(28));
        await Task.Delay(20);
        Assert.False(refresh.IsCompleted);
        Assert.Single(session.Source.SearchCalls);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(WatchRefreshDisposition.Completed, (await refresh.WaitAsync(TimeSpan.FromSeconds(5))).Disposition);
        Assert.Equal(2, session.Source.SearchCalls.Length);
    }

    [Fact]
    public async Task SuspensionReplansExpiryBeforeRecoveryAndPreservesTheLastValidBaseline()
    {
        var clock = new SplitMonitorClock();
        var expiry = new SnuWatcher.Core.WatchExpiry(new DateOnly(2026, 11, 1), "Asia/Seoul", clock.GetUtcNow().AddSeconds(5));
        var store = new FakeMonitorStore([Watch([Key()], expiry: expiry)]);
        await using var session = new Session(store, clock);
        await EventuallyAsync(() => store.SectionSnapshots.Count == 1);
        var baseline = store.SectionSnapshots[Key()].Baseline;
        session.Loop.SetSuspended(true);
        clock.Advance(TimeSpan.FromSeconds(10));
        await EventuallyAsync(() => session.Loop.Snapshot.Queries.IsEmpty);
        session.Loop.SetSuspended(false);
        clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(20);
        Assert.Single(session.Source.SearchCalls);
        Assert.Same(baseline, store.SectionSnapshots[Key()].Baseline);
    }

    [Fact]
    public async Task AFullOrThrowingObserverCannotStopPollingOrAcknowledgeAnything()
    {
        var clock = new SplitMonitorClock();
        var store = new FakeMonitorStore([Watch([Key()])]);
        await using var session = new Session(store, clock, observer: new ThrowingObserver());
        await EventuallyAsync(() => session.Loop.Snapshot.Queries.Any(query => query.State == WatchMonitorWorkState.Waiting));
        clock.Advance(TimeSpan.FromSeconds(60));
        await EventuallyAsync(() => store.CommitCommands.Length == 2);
        Assert.False(session.Running.IsCompleted);
    }

    [Fact]
    public async Task IssueStorageFailureFaultsTheHostWithoutCallingSearchOrRecordingFalseOutage()
    {
        var store = new FakeMonitorStore([Watch([Key()])]) { IssueFailure = new IOException("private fixture path") };
        await using var session = new Session(store, new());
        var failure = await Assert.ThrowsAsync<WatchMonitorException>(() => session.Running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("monitor_dependency_failed", failure.Code);
        Assert.DoesNotContain("private", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(session.Source.SearchCalls);
        Assert.Empty(store.CommitCommands);
    }

    [Fact]
    public async Task ShutdownWaitsForAnUncooperativeLogicalReadAndNeverCommitsItsCanceledResult()
    {
        var clock = new SplitMonitorClock();
        var store = new FakeMonitorStore([Watch([Key()])]);
        var read = NewRead();
        await using var session = new Session(store, clock, configure: source => source.SearchHandler = (_, _) => read.Task);
        await EventuallyAsync(() => session.Source.SearchCalls.Length == 1);
        session.Stop();
        await Task.Delay(20);
        Assert.False(session.Running.IsCompleted);
        read.SetResult(Rows(session.Source.SearchCalls[0].Query, [Key()], clock.GetUtcNow()));
        await session.Running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(store.CommitCommands);
    }

    private static SourceRead<T> Failure<T>(SourceProblemKind kind, TimeProvider clock, TimeSpan? retryAfter = null) where T : class =>
        new(SourceReadStatus.Failed, null, [new SourceProblem(kind, "source_failure", retryAfter)], clock.GetUtcNow());
    private static SourceRead<CourseCatalogData> Catalog(CatalogQuery query, DateTimeOffset at) =>
        new(SourceReadStatus.Complete, new(query, [new CatalogTerm(query.Year, "21", "Reported", null)], []), [], at);
    private static TaskCompletionSource<SourceRead<CourseSearchData>> NewRead() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class ThrowingObserver : ChannelWriter<WatchMonitorUpdate>
    {
        public override bool TryComplete(Exception? error = null) => true;
        public override bool TryWrite(WatchMonitorUpdate item) => throw new InvalidOperationException("observer failure");
        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) => new(false);
    }
}
