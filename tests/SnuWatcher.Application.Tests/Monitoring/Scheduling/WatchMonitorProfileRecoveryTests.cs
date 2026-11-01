using System.Collections.Immutable;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using Xunit;
using static SnuWatcher.Application.Tests.Monitoring.Scheduling.MonitorFixture;
using Session = SnuWatcher.Application.Tests.Monitoring.Scheduling.WatchMonitorLoopTests.Session;

namespace SnuWatcher.Application.Tests.Monitoring.Scheduling;

public sealed class WatchMonitorProfileRecoveryTests
{
    [Fact]
    public async Task FasterProfilePreservesEveryOverdueQuerysRecoveryStaggerWithoutABurst()
    {
        var clock = new SplitMonitorClock();
        var keys = new[] { Key(course: "F32.101"), Key(course: "F32.102"), Key(course: "F32.103") };
        var store = new FakeMonitorStore(keys.Select(key => Watch([key])));
        await using var session = new Session(store, clock);
        await CompletedCallsAsync(session, store, 1, 1, 3);
        clock.Advance(TimeSpan.FromSeconds(2));
        await CompletedCallsAsync(session, store, 2, 2, 3);
        clock.Advance(TimeSpan.FromSeconds(2));
        await CompletedCallsAsync(session, store, 3, 3, 3);

        session.Loop.SetOnline(false);
        await EventuallyAsync(() => session.Loop.Snapshot.ReasonCode == "offline");
        clock.Advance(TimeSpan.FromSeconds(200));
        session.Loop.SetOnline(true);
        await CompletedCallsAsync(session, store, 4, 4, 3);
        await EventuallyAsync(() => session.Loop.Snapshot.Queries.Count(query => query.LastStartedAtUtc == clock.GetUtcNow()) == 1);
        var recoveryStart = session.Source.SearchCalls[3].StartedTimestamp;

        // The other two queries are overdue under either profile, so only the independent recovery
        // fences keep them from starting alongside the first recovered query.
        await ProfileBarrierAsync(session, WatchMonitorProfile.Faster, 1);
        Assert.Equal(4, session.Source.SearchCalls.Length);
        Assert.Equal(TimeSpan.FromSeconds(2), Query(session, "F32.102").NextCheckIn);
        Assert.Equal(TimeSpan.FromSeconds(4), Query(session, "F32.103").NextCheckIn);

        clock.Advance(TimeSpan.FromSeconds(1));
        await ProfileBarrierAsync(session, WatchMonitorProfile.Faster, 2);
        Assert.Equal(4, session.Source.SearchCalls.Length);
        clock.Advance(TimeSpan.FromSeconds(1));
        await ProfileBarrierAsync(session, WatchMonitorProfile.Faster, 3);
        await CompletedCallsAsync(session, store, 5, 5, 3);
        await EventuallyAsync(() => Query(session, "F32.102").LastStartedAtUtc == clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(1));
        await ProfileBarrierAsync(session, WatchMonitorProfile.Faster, 4);
        Assert.Equal(5, session.Source.SearchCalls.Length);
        clock.Advance(TimeSpan.FromSeconds(1));
        await ProfileBarrierAsync(session, WatchMonitorProfile.Faster, 5);
        await CompletedCallsAsync(session, store, 6, 6, 3);
        await EventuallyAsync(() => Query(session, "F32.103").LastStartedAtUtc == clock.GetUtcNow());

        var recovery = session.Source.SearchCalls.Skip(3).ToImmutableArray();
        Assert.Equal(new[] { "F32.101", "F32.102", "F32.103" }, recovery.Select(call => call.Query.CourseCode));
        Assert.Equal(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) },
            recovery.Select(call => clock.GetElapsedTime(recoveryStart, call.StartedTimestamp)));
        Assert.All(recovery, call => Assert.Equal(SourceRequestPurpose.Watch, call.Purpose));
    }

    [Fact]
    public async Task ProfileSwitchesLeaveCatalogDeadlineAndOriginalCachedReceiptUnchanged()
    {
        var clock = new SplitMonitorClock();
        var originalReceipt = clock.GetUtcNow().AddMinutes(-5);
        var store = new FakeMonitorStore([Watch([Key()])]);
        await using var session = new Session(store, clock,
            options: new(WatchMonitorProfile.Faster, jitterPercent: 0, catalogRefreshInterval: TimeSpan.FromSeconds(30)),
            configure: source => source.CatalogHandler = (query, _) => Task.FromResult(
                new SourceRead<CourseCatalogData>(SourceReadStatus.Complete,
                    new(query, [new CatalogTerm(query.Year, "21", "Cached reported term", null)], []), [], originalReceipt)));
        await CompletedCallsAsync(session, store, 1, 1, 1);
        Assert.Equal(originalReceipt, Assert.Single(session.Loop.Snapshot.Catalogs).LastSuccessfulAtUtc);

        clock.Advance(TimeSpan.FromSeconds(5));
        await ProfileBarrierAsync(session, WatchMonitorProfile.LowActivity, 1);
        Assert.Single(session.Source.CatalogCalls);
        clock.Advance(TimeSpan.FromSeconds(5));
        await ProfileBarrierAsync(session, WatchMonitorProfile.Faster, 2);
        Assert.Single(session.Source.CatalogCalls);
        clock.Advance(TimeSpan.FromSeconds(19));
        await ProfileBarrierAsync(session, WatchMonitorProfile.LowActivity, 3);
        Assert.Single(session.Source.CatalogCalls);
        Assert.Equal(originalReceipt, Assert.Single(session.Loop.Snapshot.Catalogs).LastSuccessfulAtUtc);

        clock.Advance(TimeSpan.FromSeconds(1));
        await ProfileBarrierAsync(session, WatchMonitorProfile.LowActivity, 4);
        await EventuallyAsync(() => session.Source.CatalogCalls.Length == 2);
        // The second barrier publishes after the already-completed catalog job has been drained,
        // so this receipt assertion cannot accidentally inspect the first catalog's old snapshot.
        await ProfileBarrierAsync(session, WatchMonitorProfile.LowActivity, 5);
        Assert.Equal(originalReceipt, Assert.Single(session.Loop.Snapshot.Catalogs).LastSuccessfulAtUtc);

        clock.Advance(TimeSpan.FromSeconds(29));
        await ProfileBarrierAsync(session, WatchMonitorProfile.LowActivity, 6);
        Assert.Equal(2, session.Source.CatalogCalls.Length);
        clock.Advance(TimeSpan.FromSeconds(1));
        await ProfileBarrierAsync(session, WatchMonitorProfile.LowActivity, 7);
        await EventuallyAsync(() => session.Source.CatalogCalls.Length == 3);
        await ProfileBarrierAsync(session, WatchMonitorProfile.LowActivity, 8);
        Assert.Equal(originalReceipt, Assert.Single(session.Loop.Snapshot.Catalogs).LastSuccessfulAtUtc);
        Assert.Single(session.Source.SearchCalls);
        Assert.Equal(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60) },
            session.Source.CatalogCalls.Select(call => clock.GetElapsedTime(session.Source.CatalogCalls[0].StartedTimestamp, call.StartedTimestamp)));
    }

    [Fact]
    public async Task AddedSectionKeepsItsRequestedFloorThroughASlowerProfileWithoutExtendingTheOldScope()
    {
        var clock = new SplitMonitorClock();
        var originalKey = Key();
        var addedKey = Key(section: "013");
        var originalWatch = Watch([originalKey]);
        var store = new FakeMonitorStore([originalWatch]);
        var heldRead = new TaskCompletionSource<SourceRead<CourseSearchData>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        CancellationToken originalSourceToken = default;
        await using var session = new Session(store, clock, configure: source => source.SearchHandler = (query, token) =>
        {
            if (Interlocked.Increment(ref calls) != 1) return Task.FromResult(Rows(query, [originalKey, addedKey], clock.GetUtcNow()));
            originalSourceToken = token;
            return heldRead.Task;
        });
        try
        {
            await EventuallyAsync(() => session.Source.SearchCalls.Length == 1);
            var originalAttempt = Assert.Single(store.Issues);
            store.Watches = [originalWatch, Watch([addedKey])];
            session.Loop.Wake();
            await EventuallyAsync(() => session.Loop.Snapshot.Queries.Any(query => query.Scope.Contains(addedKey)));
            clock.Advance(TimeSpan.FromSeconds(5));
            await ProfileBarrierAsync(session, WatchMonitorProfile.LowActivity, 1);
            Assert.False(originalSourceToken.IsCancellationRequested);
            Assert.Single(session.Source.SearchCalls);
            Assert.Equal<SectionKey>([originalKey], Assert.Single(store.Issues).Scope);

            clock.Advance(TimeSpan.FromSeconds(5));
            heldRead.SetResult(Rows(session.Source.SearchCalls[0].Query, [originalKey], clock.GetUtcNow()));
            await CompletedCallsAsync(session, store, 1, 1, 1);
            Assert.Equal(TimeSpan.FromSeconds(20), session.Loop.Snapshot.Queries.Single().NextCheckIn);
            Assert.Equal(originalAttempt.Token, Assert.Single(store.CommitCommands).Completion.Token);
            Assert.Equal(originalKey, store.CommitCommands[0].Completion.Key);

            clock.Advance(TimeSpan.FromSeconds(19));
            await ProfileBarrierAsync(session, WatchMonitorProfile.LowActivity, 2);
            Assert.Single(session.Source.SearchCalls);
            clock.Advance(TimeSpan.FromSeconds(1));
            await ProfileBarrierAsync(session, WatchMonitorProfile.LowActivity, 3);
            await CompletedCallsAsync(session, store, 2, 3, 1);
            await EventuallyAsync(() => session.Loop.Snapshot.Queries.Single().LastStartedAtUtc == clock.GetUtcNow());

            Assert.Equal(2, store.Issues.Length);
            Assert.NotEqual(originalAttempt.Token, store.Issues[1].Token);
            Assert.Equal<SectionKey>([originalKey], store.Issues[0].Scope);
            Assert.Equal<SectionKey>([originalKey, addedKey], store.Issues[1].Scope);
            Assert.Equal(TimeSpan.FromSeconds(30), clock.GetElapsedTime(session.Source.SearchCalls[0].StartedTimestamp,
                session.Source.SearchCalls[1].StartedTimestamp));
            Assert.Equal<SectionKey>([originalKey, addedKey], store.CommitCommands.Skip(1).Select(command => command.Completion.Key));
            Assert.All(store.CommitCommands.Skip(1), command => Assert.Equal(store.Issues[1].Token, command.Completion.Token));
            Assert.Equal(TimeSpan.FromSeconds(120), session.Loop.Snapshot.Queries.Single().NextCheckIn);
        }
        finally
        {
            if (!session.Source.SearchCalls.IsEmpty)
                heldRead.TrySetResult(Rows(session.Source.SearchCalls[0].Query, [originalKey], clock.GetUtcNow()));
        }
    }

    // Same-profile revision acknowledgements are actor publication barriers, not timing changes.
    private static Task<WatchMonitorProfileSnapshot> ProfileBarrierAsync(Session session, WatchMonitorProfile profile, long revision) =>
        session.Loop.SetRefreshProfileAsync(profile, revision, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

    private static Task CompletedCallsAsync(Session session, FakeMonitorStore store, int calls, int commits, int queries) =>
        EventuallyAsync(() => session.Source.SearchCalls.Length == calls && store.CommitCommands.Length == commits
            && session.Source.Active == 0 && session.Loop.Snapshot.Queries.Length == queries
            && session.Loop.Snapshot.Queries.All(query => query.State == WatchMonitorWorkState.Waiting)
            && session.Loop.Snapshot.Queries.Count(query => query.LastStartedAtUtc is not null) == Math.Min(calls, queries));

    private static WatchMonitorQuerySnapshot Query(Session session, string course) =>
        session.Loop.Snapshot.Queries.Single(query => query.Query.CourseCode == course);
}
