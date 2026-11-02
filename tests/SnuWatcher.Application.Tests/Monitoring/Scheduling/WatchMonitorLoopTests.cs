using System.Collections.Immutable;
using System.Threading.Channels;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using Xunit;
using static SnuWatcher.Application.Tests.Monitoring.Scheduling.MonitorFixture;

namespace SnuWatcher.Application.Tests.Monitoring.Scheduling;

public sealed class WatchMonitorLoopTests
{
    [Fact]
    public async Task NoWatchesMakesNoCatalogRequestsAndRunHasOneLifetime()
    {
        var clock = new SplitMonitorClock();
        var store = new FakeMonitorStore();
        await using var session = new Session(store, clock);
        await EventuallyAsync(() => store.ReadCount > 0);
        Assert.Empty(session.Source.CatalogCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.Loop.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ResponseLatencyAndUtcChangesDoNotMoveStartBasedDueTime()
    {
        var clock = new SplitMonitorClock();
        var store = new FakeMonitorStore([Watch([Key()])]);
        var pending = NewRead();
        await using var session = new Session(store, clock, configure: source => source.SearchHandler = (_, _) => pending.Task);
        await EventuallyAsync(() => session.Source.SearchCalls.Length == 1);
        clock.Advance(TimeSpan.FromSeconds(20));
        pending.SetResult(Rows(session.Source.SearchCalls[0].Query, [Key()], clock.GetUtcNow()));
        await EventuallyAsync(() => store.CommitCommands.Length == 1);
        clock.ShiftUtc(TimeSpan.FromDays(5));
        clock.Advance(TimeSpan.FromSeconds(39));
        await Task.Delay(20);
        Assert.Single(session.Source.SearchCalls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => session.Source.SearchCalls.Length == 2);
        Assert.Equal(TimeSpan.FromSeconds(60), clock.GetElapsedTime(session.Source.SearchCalls[0].StartedTimestamp,
            session.Source.SearchCalls[1].StartedTimestamp));
    }

    [Fact]
    public async Task ALongOverrunProducesOneDueAttemptWithoutAccumulatingRunDebt()
    {
        var clock = new SplitMonitorClock();
        var store = new FakeMonitorStore([Watch([Key()])]);
        var first = NewRead();
        var second = NewRead();
        var calls = 0;
        await using var session = new Session(store, clock, configure: source =>
            source.SearchHandler = (_, _) => Interlocked.Increment(ref calls) == 1 ? first.Task : second.Task);
        await EventuallyAsync(() => session.Source.SearchCalls.Length == 1);
        clock.Advance(TimeSpan.FromSeconds(150));
        first.SetResult(Rows(session.Source.SearchCalls[0].Query, [Key()], clock.GetUtcNow()));
        await EventuallyAsync(() => session.Source.SearchCalls.Length == 2);
        second.SetResult(Rows(session.Source.SearchCalls[1].Query, [Key()], clock.GetUtcNow()));
        await EventuallyAsync(() => store.CommitCommands.Length == 2);
        await Task.Delay(20);
        Assert.Equal(2, session.Source.SearchCalls.Length);
    }

    [Fact]
    public async Task EveryExactIssueScopeIsPersistedBeforeOneWatchSourceCall()
    {
        var clock = new SplitMonitorClock();
        var keys = new[] { Key(semester: "1", detail: "21"), Key(semester: "12", detail: "1") };
        var store = new FakeMonitorStore(keys.Select(key => Watch([key])));
        await using var session = new Session(store, clock, configure: source =>
        {
            source.CatalogHandler = (query, _) => Task.FromResult(new SourceRead<CourseCatalogData>(SourceReadStatus.Complete,
                new(query, [new CatalogTerm(query.Year, "121", "Reported", null)], []), [], clock.GetUtcNow()));
            source.SearchHandler = (query, _) =>
            {
                Assert.Equal(2, store.Issues.Length);
                Assert.All(store.Issues, issued => Assert.Single(issued.Scope));
                return Task.FromResult(Rows(query, keys, clock.GetUtcNow()));
            };
        });
        await EventuallyAsync(() => store.CommitCommands.Length == 2);
        Assert.Single(session.Source.SearchCalls);
        Assert.Equal(SourceRequestPurpose.Watch, session.Source.SearchCalls[0].Purpose);
        Assert.Equal(2, store.CommitCommands.Select(command => command.Completion.Token).Distinct().Count());
    }

    [Fact]
    public async Task ManualRefreshJoinsAndCallerCancellationOnlyDetachesItsWaiter()
    {
        var clock = new SplitMonitorClock();
        var store = new FakeMonitorStore([Watch([Key()])]);
        var read = NewRead();
        await using var session = new Session(store, clock, configure: source => source.SearchHandler = (_, _) => read.Task);
        await EventuallyAsync(() => session.Source.SearchCalls.Length == 1);
        var joined = session.Loop.RefreshAsync(Key(), CancellationToken.None);
        using var canceled = new CancellationTokenSource();
        var detached = session.Loop.RefreshAsync(Key(), canceled.Token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => detached);
        Assert.Single(store.Issues);
        read.SetResult(Rows(session.Source.SearchCalls[0].Query, [Key()], clock.GetUtcNow()));
        Assert.Equal(WatchRefreshDisposition.Completed, (await joined.WaitAsync(TimeSpan.FromSeconds(5))).Disposition);
        Assert.Single(session.Source.SearchCalls);
    }

    [Fact]
    public async Task RepeatedLifecycleWakeKeepsOldScopeAndSchedulesOneFreshFollowupAtTheFloor()
    {
        var clock = new SplitMonitorClock();
        var firstKey = Key();
        var addedKey = Key(section: "013");
        var original = Watch([firstKey]);
        var store = new FakeMonitorStore([original]);
        var read = NewRead();
        var calls = 0;
        await using var session = new Session(store, clock, configure: source => source.SearchHandler = (query, _) =>
            Interlocked.Increment(ref calls) == 1 ? read.Task : Task.FromResult(Rows(query, [firstKey, addedKey], clock.GetUtcNow())));
        await EventuallyAsync(() => session.Source.SearchCalls.Length == 1);
        store.Watches = [original, Watch([addedKey])];
        for (var index = 0; index < 100; index++) session.Loop.Wake();
        read.SetResult(Rows(session.Source.SearchCalls[0].Query, [firstKey], clock.GetUtcNow()));
        await EventuallyAsync(() => store.CommitCommands.Length == 1);
        Assert.Equal<SectionKey>([firstKey], store.Issues[0].Scope);
        clock.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(20);
        Assert.Single(session.Source.SearchCalls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => session.Source.SearchCalls.Length == 2);
        await EventuallyAsync(() => store.CommitCommands.Length == 3);
        Assert.Equal<SectionKey>([firstKey, addedKey], store.Issues[1].Scope);
        Assert.Equal(2, session.Source.SearchCalls.Length);
    }

    [Fact]
    public async Task RenameDoesNotRequestAnotherAttemptButResumeSameKeyNeedsFreshIssuance()
    {
        var clock = new SplitMonitorClock();
        var first = Watch([Key()]);
        var store = new FakeMonitorStore([first]);
        var read = NewRead();
        var calls = 0;
        await using var session = new Session(store, clock, configure: source => source.SearchHandler = (query, _) =>
            Interlocked.Increment(ref calls) == 1 ? read.Task : Task.FromResult(Rows(query, [Key()], clock.GetUtcNow())));
        await EventuallyAsync(() => store.Issues.Length == 1);
        store.Watches = [Watch([Key()], id: first.Id, generation: 2, activationWatermark: 1, label: "Resumed")];
        session.Loop.Wake();
        read.SetResult(Rows(session.Source.SearchCalls[0].Query, [Key()], clock.GetUtcNow()));
        await EventuallyAsync(() => store.CommitCommands.Length == 1);
        Assert.Empty(store.CommitCommands[0].Effects.Initializations);
        clock.Advance(TimeSpan.FromSeconds(30));
        await EventuallyAsync(() => store.Issues.Length == 2);
        await EventuallyAsync(() => store.CommitCommands.Length == 2);
        Assert.Equal(2, Assert.Single(store.CommitCommands[1].Effects.Initializations).Generation);
        store.Watches = [Watch([Key()], id: first.Id, generation: 2, activationWatermark: 1,
            initializedByAttemptOrdinal: 2, label: "Renamed", definitionRevision: 4)];
        session.Loop.Wake();
        clock.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(20);
        Assert.Equal(2, session.Source.SearchCalls.Length);
    }

    [Fact]
    public async Task PartialRowsCommitIndependentlyAndCasRetryRetainsExactSourceFacts()
    {
        var clock = new SplitMonitorClock();
        var keys = new[] { Key(), Key(section: "013") };
        var store = new FakeMonitorStore([Watch(keys)]) { ConflictWritesRemaining = 1 };
        await using var session = new Session(store, clock, configure: source => source.SearchHandler = (query, _) =>
        {
            var data = Rows(query, [keys[0]], clock.GetUtcNow()).Data!;
            return Task.FromResult(new SourceRead<CourseSearchData>(SourceReadStatus.Partial, data,
                [new SourceProblem(SourceProblemKind.RateLimited, "source_rate_limited", TimeSpan.FromMinutes(2))], clock.GetUtcNow()));
        });
        await EventuallyAsync(() => store.SectionSnapshots.Count == 2);
        Assert.Equal(CheckOutcome.Success, store.SectionSnapshots[keys[0]].LatestOutcome);
        Assert.Equal(CheckOutcome.Incomplete, store.SectionSnapshots[keys[1]].LatestOutcome);
        Assert.Single(session.Source.SearchCalls);
        Assert.Equal(store.CommitCommands[0].Completion, store.CommitCommands[1].Completion);
    }

    [Fact]
    public async Task ExhaustedCasBurstRetainsCompletionAndRetriesCommitWithoutRefetch()
    {
        var clock = new SplitMonitorClock();
        var store = new FakeMonitorStore([Watch([Key()])]) { ConflictWritesRemaining = 3 };
        await using var session = new Session(store, clock);
        await EventuallyAsync(() => session.Loop.Snapshot.Queries.Any(query => query.State == WatchMonitorWorkState.CommitDelayed));
        Assert.Equal(3, store.CommitCommands.Length);
        var original = store.CommitCommands[0].Completion;
        clock.Advance(TimeSpan.FromSeconds(5));
        await EventuallyAsync(() => store.SectionSnapshots.Count == 1);
        Assert.Single(session.Source.SearchCalls);
        Assert.All(store.CommitCommands, command => Assert.Equal(original, command.Completion));
    }

    [Fact]
    public async Task OfflineCancelsAndAwaitsActualOwnedJobBeforeRecoveryWithoutFabricatingOutage()
    {
        var clock = new SplitMonitorClock();
        var store = new FakeMonitorStore([Watch([Key()])]);
        var read = NewRead();
        CancellationToken requestToken = default;
        await using var session = new Session(store, clock, configure: source => source.SearchHandler = (_, token) =>
        { requestToken = token; return read.Task; });
        await EventuallyAsync(() => session.Source.SearchCalls.Length == 1);
        session.Loop.SetOnline(false);
        await EventuallyAsync(() => requestToken.IsCancellationRequested);
        session.Loop.SetOnline(true);
        clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(20);
        Assert.Single(session.Source.SearchCalls);
        read.SetResult(Rows(session.Source.SearchCalls[0].Query, [Key()], clock.GetUtcNow()));
        await EventuallyAsync(() => session.Source.SearchCalls.Length == 2);
        Assert.Equal(1, session.Source.MaxActive);
        Assert.DoesNotContain(store.CommitCommands, command => command.Completion.Token == store.Issues[0].Token);
    }

    [Fact]
    public async Task PersistedAccessPauseBlocksCatalogsAndManualRefreshUntilExplicitWakeAfterResume()
    {
        var clock = new SplitMonitorClock();
        var store = new FakeMonitorStore([Watch([Key()])]);
        var policy = new FakeMonitorPolicy
        {
            Snapshot = new(SourcePolicyIds.SnuSugang, 1,
                new SourceRequestProgress(1, clock.GetUtcNow(), 1, SourceRequestOutcome.AccessRestricted, clock.GetUtcNow(), 0, null),
                new SourceAccessPause(1, clock.GetUtcNow(), "access_restricted")),
        };
        await using var session = new Session(store, clock, policy: policy);
        await EventuallyAsync(() => session.Loop.Snapshot.ReasonCode == "source_access_restricted");
        Assert.Empty(session.Source.CatalogCalls);
        Assert.Equal(WatchRefreshDisposition.Deferred, (await session.Loop.RefreshAsync(Key(), CancellationToken.None)).Disposition);
        policy.Snapshot = SourcePolicySnapshot.Initial(SourcePolicyIds.SnuSugang);
        session.Loop.Wake();
        await EventuallyAsync(() => session.Source.SearchCalls.Length == 1);
    }

    [Fact]
    public async Task PersistedCooldownHasAMonotonicFloorDespiteForwardUtcJump()
    {
        var clock = new SplitMonitorClock();
        var store = new FakeMonitorStore([Watch([Key()])]);
        var policy = new FakeMonitorPolicy { Snapshot = new(SourcePolicyIds.SnuSugang, 1,
            SourceRequestProgress.Empty, cooldown: new SourceCooldown(clock.GetUtcNow(), TimeSpan.FromMinutes(2))) };
        await using var session = new Session(store, clock, policy: policy);
        await EventuallyAsync(() => session.Loop.Snapshot.ReasonCode == "source_cooldown");
        clock.ShiftUtc(TimeSpan.FromDays(1));
        session.Loop.Wake();
        clock.Advance(TimeSpan.FromSeconds(119));
        await Task.Delay(20);
        Assert.Empty(session.Source.CatalogCalls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => session.Source.SearchCalls.Length == 1);
    }

    private static TaskCompletionSource<SourceRead<CourseSearchData>> NewRead() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal sealed class Session : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        internal Session(FakeMonitorStore store, SplitMonitorClock clock, Action<FakeMonitorSource>? configure = null,
            FakeMonitorPolicy? policy = null, WatchMonitorOptions? options = null, ChannelWriter<WatchMonitorUpdate>? observer = null)
        {
            Source = new(store, clock);
            configure?.Invoke(Source);
            Loop = new(store, Source, policy ?? new FakeMonitorPolicy(), clock,
                options ?? new WatchMonitorOptions(jitterPercent: 0), observer);
            Running = Loop.RunAsync(_stop.Token);
        }
        internal FakeMonitorSource Source { get; }
        internal WatchMonitorLoop Loop { get; }
        internal Task Running { get; }
        internal void Stop() => _stop.Cancel();
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (!Running.IsFaulted) await Running.WaitAsync(TimeSpan.FromSeconds(5));
            _stop.Dispose();
        }
    }
}
