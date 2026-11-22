using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Migration;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Persistence;
using SnuWatcher.Integration.Tests.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests;

public sealed class WatchMonitorLoopTests
{
    [Fact]
    public async Task OverlappingGroupAndIndividualShareOneDurableAttemptAndOneEventPerOpeningSection()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var first = StoreFixture.Key();
        var second = StoreFixture.Key("013");
        var individual = await fixture.AddAsync(first, initialNotice: false);
        var group = await fixture.Store.CreateSubscriptionAsync(new(Guid.NewGuid(), "Any section", SubscriptionKind.Group,
            [first, second], true, null, fixture.Clock.GetUtcNow()), default);
        await using var run = new MonitorRun(fixture) { Source = { Registered = 120 } };
        run.Start();
        await UntilAsync(async () => (await fixture.Store.ReadSectionAsync(second, default)).LatestCompletedOrdinal == 1);
        Assert.Single(run.Source.Searches);
        Assert.Equal(1, (await fixture.Store.ReadSectionAsync(first, default)).LatestCompletedOrdinal);
        Assert.All((await fixture.Store.FindSubscriptionAsync(group.Id, default))!.Members,
            member => Assert.Equal(1, member.InitializedByAttemptOrdinal));
        Assert.Empty(await fixture.Store.ReadPendingDeliveriesAsync(10, default));

        run.Source.Registered = 119;
        run.Advance(TimeSpan.FromSeconds(30));
        await UntilAsync(async () => (await fixture.Store.ReadSectionAsync(second, default)).LatestCompletedOrdinal == 2);
        var pending = await fixture.Store.ReadPendingDeliveriesAsync(10, default);
        Assert.Equal(2, pending.Length);
        var opening = Assert.Single(pending.Where(delivery => delivery.Key == first));
        Assert.Equal(first, opening.Key);
        Assert.Equal(new[] { individual.Id, group.Id }.Order(), opening.Trigger.Recipients
            .Select(recipient => Guid.ParseExact(recipient.SubscriptionId, "N")).Order());
        Assert.Equal(2, run.Source.Searches.Length);
    }

    [Fact]
    public async Task EveryFullTermScopeIsCommittedBeforeOneSharedGatewayRead()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var first = new SectionKey(2026, "1", "21", "F32.102", "012", "000");
        var second = new SectionKey(2026, "12", "1", "F32.102", "012", "000");
        await fixture.AddAsync(first);
        await fixture.AddAsync(second);
        await using var run = new MonitorRun(fixture);
        long issuedBeforeRead = 0;
        run.Source.Handler = async (query, token) =>
        {
            using var connection = new SqliteConnectionFactory(fixture.DatabasePath).Open(SqliteOpenMode.ReadOnly);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM IssuedChecks;";
            issuedBeforeRead = (long)command.ExecuteScalar()!;
            return await run.Source.CurrentRowsAsync(query, token);
        };
        run.Start();
        await UntilAsync(async () => (await fixture.Store.ReadSectionAsync(second, default)).Baseline is not null);
        Assert.Equal(2, issuedBeforeRead);
        Assert.Single(run.Source.Searches);
        var ordinals = new[] { (await fixture.Store.ReadSectionAsync(first, default)).LatestCompletedOrdinal,
            (await fixture.Store.ReadSectionAsync(second, default)).LatestCompletedOrdinal };
        Assert.Equal(new long[] { 1, 2 }, ordinals.Order());
    }

    [Fact]
    public async Task WatchAddedDuringAReadWaitsForItsOwnIssuedAttemptAndGetsOnlyItsOwnInitialEvent()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var first = StoreFixture.Key();
        var addedKey = StoreFixture.Key("013");
        var original = await fixture.AddAsync(first);
        var read = NewRead();
        await using var run = new MonitorRun(fixture);
        run.Source.Handler = (query, token) => run.Source.Searches.Length == 1 ? read.Task : run.Source.CurrentRowsAsync(query, token);
        run.Start();
        await UntilAsync(() => Task.FromResult(run.Source.Searches.Length == 1));
        var added = await fixture.AddAsync(addedKey);
        run.Loop.Wake();
        read.SetResult(Rows(run.Source.Searches[0], [first], fixture.Clock.GetUtcNow()));
        await UntilAsync(async () => (await fixture.Store.ReadSectionAsync(first, default)).Baseline is not null);
        Assert.Equal(0, (await fixture.Store.ReadSectionAsync(addedKey, default)).LatestCompletedOrdinal);
        Assert.Null(Assert.Single((await fixture.Store.FindSubscriptionAsync(added.Id, default))!.Members).InitializedByAttemptOrdinal);

        run.Advance(TimeSpan.FromSeconds(30));
        await UntilAsync(async () => (await fixture.Store.ReadSectionAsync(addedKey, default)).Baseline is not null);
        var events = await fixture.Store.ReadPendingDeliveriesAsync(10, default);
        Assert.Equal(2, events.Length);
        Assert.Equal(new[] { original.Id, added.Id }.Order(), events.Select(delivery =>
            Guid.ParseExact(Assert.Single(delivery.Trigger.Recipients).SubscriptionId, "N")).Order());
        Assert.Equal(2, (await fixture.Store.ReadSectionAsync(addedKey, default)).LatestCompletedOrdinal);
    }

    [Fact]
    public async Task ResumeDuringAReadCannotBeInitializedByTheOldToken()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        var watch = await fixture.AddAsync(key);
        var read = NewRead();
        await using var run = new MonitorRun(fixture);
        run.Source.Handler = (query, token) => run.Source.Searches.Length == 1 ? read.Task : run.Source.CurrentRowsAsync(query, token);
        run.Start();
        await UntilAsync(() => Task.FromResult(run.Source.Searches.Length == 1));
        var paused = await fixture.Store.ChangeSubscriptionAsync(new PauseSubscription(watch.Id, watch.DefinitionRevision, fixture.Clock.GetUtcNow()), default);
        var resumed = await fixture.Store.ChangeSubscriptionAsync(new ResumeSubscription(watch.Id, paused.DefinitionRevision, fixture.Clock.GetUtcNow()), default);
        run.Loop.Wake();
        read.SetResult(Rows(run.Source.Searches[0], [key], fixture.Clock.GetUtcNow()));
        await UntilAsync(async () => (await fixture.Store.ReadSectionAsync(key, default)).LatestCompletedOrdinal == 1);
        Assert.Null(Assert.Single((await fixture.Store.FindSubscriptionAsync(watch.Id, default))!.Members).InitializedByAttemptOrdinal);
        Assert.Empty(await fixture.Store.ReadPendingDeliveriesAsync(10, default));
        run.Advance(TimeSpan.FromSeconds(30));
        await UntilAsync(async () => (await fixture.Store.ReadSectionAsync(key, default)).LatestCompletedOrdinal == 2);
        var opening = Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(10, default));
        Assert.Equal(resumed.ActivationGeneration, Assert.Single(opening.Trigger.Recipients).Generation);
        Assert.Equal(2, Assert.Single((await fixture.Store.FindSubscriptionAsync(watch.Id, default))!.Members).InitializedByAttemptOrdinal);
    }

    [Fact]
    public async Task PartialResponsePreservesMissingSectionsBaselineAndCommitsOnlyVerifiedOpening()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var first = StoreFixture.Key();
        var missing = StoreFixture.Key("013");
        await fixture.AddAsync(first);
        await fixture.AddAsync(missing);
        await fixture.ObserveAsync(first, 120);
        await fixture.ObserveAsync(missing, 120);
        var previous = (await fixture.Store.ReadSectionAsync(missing, default)).Baseline;
        await using var run = new MonitorRun(fixture);
        run.Source.Handler = (query, _) => Task.FromResult(new SourceRead<CourseSearchData>(SourceReadStatus.Partial,
            Rows(query, [first], fixture.Clock.GetUtcNow()).Data,
            [new SourceProblem(SourceProblemKind.RateLimited, "source_rate_limited", TimeSpan.FromMinutes(2))], fixture.Clock.GetUtcNow()));
        run.Start();
        await UntilAsync(async () => (await fixture.Store.ReadSectionAsync(missing, default)).LatestCompletedOrdinal == 3);
        var delayed = await fixture.Store.ReadSectionAsync(missing, default);
        Assert.Equal(CheckOutcome.Incomplete, delayed.LatestOutcome);
        Assert.Equal(previous, delayed.Baseline);
        Assert.Equal(first, Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(10, default)).Key);
        Assert.Single(run.Source.Searches);
    }

    [Fact]
    public async Task ActualCatalogRevisionConflictRetriesTheSameCompletionWithoutAnotherFetch()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var watch = await fixture.AddAsync(StoreFixture.Key());
        var store = new ConflictOnceStore(fixture.Store, watch.Id, fixture.Clock);
        await using var run = new MonitorRun(fixture, store);
        run.Start();
        await UntilAsync(async () => (await fixture.Store.ReadSectionAsync(StoreFixture.Key(), default)).Baseline is not null);
        Assert.Equal(2, store.Completions.Length);
        Assert.Equal(store.Completions[0], store.Completions[1]);
        Assert.Single(run.Source.Searches);
        Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(10, default));
    }

    [Fact]
    public async Task Persisted403BlocksANewLoopAfterReopenUntilExplicitResume()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.AddAsync(StoreFixture.Key());
        var policy = (ISourcePolicyStore)fixture.Store;
        var issued = (await policy.TryIssueSourceRequestAsync(new(SourcePolicyIds.SnuSugang, SourceRequestKind.Catalog), default)).Issued!;
        await policy.RecordSourceRequestResultAsync(new(issued.Token, SourceRequestOutcome.AccessRestricted, fixture.Clock.GetUtcNow()), default);
        await fixture.ReopenAsync();
        await using var run = new MonitorRun(fixture);
        run.Start();
        await UntilAsync(() => Task.FromResult(run.Loop.Snapshot.ReasonCode == "source_access_restricted"));
        Assert.Equal(0, run.Source.CatalogCount);
        Assert.Empty(run.Source.Searches);
        var current = await ((ISourcePolicyStore)fixture.Store).ReadSourcePolicyAsync(SourcePolicyIds.SnuSugang, default);
        await ((ISourcePolicyStore)fixture.Store).ResumeSourceAccessAsync(new(SourcePolicyIds.SnuSugang, current.Revision), default);
        run.Loop.Wake();
        await UntilAsync(async () => (await fixture.Store.ReadSectionAsync(StoreFixture.Key(), default)).Baseline is not null);
        Assert.Single(run.Source.Searches);
    }

    private static SourceRead<CourseSearchData> Rows(CourseSearchQuery query, IEnumerable<SectionKey> keys, DateTimeOffset at, int registered = 119) =>
        new(SourceReadStatus.Complete, new(query, keys.Select(key => new CourseSearchRow(key, "Course", null, null, null,
            registered, 120, null, "Enrollment/Quota", at)), 1), [], at);
    private static TaskCompletionSource<SourceRead<CourseSearchData>> NewRead() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!await condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("The local monitor integration condition did not complete.");
            await Task.Delay(5);
        }
    }

    private sealed class MonitorRun : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly StoreFixture _fixture;
        private readonly WakeClock _clock;
        private Task? _running;
        internal MonitorRun(StoreFixture fixture, IWatchStore? store = null)
        {
            _fixture = fixture;
            _clock = new(fixture.Clock);
            Source = new(fixture.Store, _clock);
            Loop = new(store ?? fixture.Store, Source, (ISourcePolicyStore)fixture.Store, _clock,
                new(WatchMonitorProfile.Faster, jitterPercent: 0));
        }
        internal LocalSource Source { get; }
        internal WatchMonitorLoop Loop { get; }
        internal void Start() => _running = Loop.RunAsync(_stop.Token);
        internal void Advance(TimeSpan interval) { _fixture.Clock.Advance(interval); _clock.Advance(interval); Loop.Wake(); }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (_running is not null) await _running.WaitAsync(TimeSpan.FromSeconds(10));
            _stop.Dispose();
        }
    }

    // Tests explicitly Wake after advancing. Real timers remain bounded; no timing package/project change is needed.
    private sealed class WakeClock(StoreClock utc) : TimeProvider
    {
        private long _extra;
        public override DateTimeOffset GetUtcNow() => utc.GetUtcNow();
        public override long GetTimestamp() => Stopwatch.GetTimestamp() + Interlocked.Read(ref _extra);
        public override long TimestampFrequency => Stopwatch.Frequency;
        internal void Advance(TimeSpan interval) => Interlocked.Add(ref _extra, checked((long)(interval.TotalSeconds * TimestampFrequency)));
    }

    private sealed class LocalSource(SqliteWatchStore store, TimeProvider clock) : ICourseSource
    {
        private ImmutableArray<CourseSearchQuery> _searches = [];
        private readonly object _sync = new();
        private int _catalogCount;
        internal int CatalogCount => Volatile.Read(ref _catalogCount);
        internal ImmutableArray<CourseSearchQuery> Searches { get { lock (_sync) return _searches; } }
        internal int Registered { get; set; } = 119;
        internal Func<CourseSearchQuery, CancellationToken, Task<SourceRead<CourseSearchData>>>? Handler { get; set; }
        public async Task<SourceRead<CourseCatalogData>> GetCatalogAsync(CatalogQuery query, SourceRequestPurpose purpose, CancellationToken token)
        {
            Interlocked.Increment(ref _catalogCount);
            var watches = await store.ListSubscriptionsAsync(token);
            var selectors = watches.SelectMany(watch => watch.Members).Select(member => member.Key)
                .Where(key => key.Year == query.Year).Select(key => key.SemesterCode + key.DetailSemesterCode).Distinct();
            return new(SourceReadStatus.Complete, new(query,
                selectors.Select(selector => new CatalogTerm(query.Year, selector, "Owned fixture term", null)), []), [], clock.GetUtcNow());
        }
        public Task<SourceRead<CourseSearchData>> SearchAsync(CourseSearchQuery query, SourceRequestPurpose purpose, CancellationToken token)
        {
            Assert.Equal(SourceRequestPurpose.Watch, purpose);
            lock (_sync) _searches = _searches.Add(query);
            return Handler is null ? CurrentRowsAsync(query, token) : Handler(query, token);
        }
        internal async Task<SourceRead<CourseSearchData>> CurrentRowsAsync(CourseSearchQuery query, CancellationToken token)
        {
            var watches = await store.ListSubscriptionsAsync(token);
            var keys = watches.SelectMany(watch => watch.Members.Select(member => member.Key)).Distinct()
                .Where(key => key.Year == query.Year && key.CourseCode == query.CourseCode
                    && key.SemesterCode + key.DetailSemesterCode == query.TermSelector);
            return Rows(query, keys, clock.GetUtcNow(), Registered);
        }
        public Task<SourceRead<CourseDetails>> GetDetailsAsync(SectionKey key, SourceRequestPurpose purpose, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class ConflictOnceStore(SqliteWatchStore inner, Guid watchId, TimeProvider clock) : IWatchStore
    {
        internal ImmutableArray<SectionCompletion> Completions { get; private set; } = [];
        public async Task<TryCommitResult> TryCommitSectionAsync(PreparedSectionCommit command, CancellationToken token)
        {
            Completions = Completions.Add(command.Completion);
            if (Completions.Length == 1)
            {
                var watch = (await inner.FindSubscriptionAsync(watchId, token))!;
                await inner.ChangeSubscriptionAsync(new RenameSubscription(watchId, watch.DefinitionRevision, clock.GetUtcNow(), "Concurrent rename"), token);
            }
            return await inner.TryCommitSectionAsync(command, token);
        }
        public Task<IssuedCheck> IssueCheckAsync(CheckIssue issue, CancellationToken token) => inner.IssueCheckAsync(issue, token);
        public Task<SubscriptionSnapshot> CreateSubscriptionAsync(NewSubscription command, CancellationToken token) => inner.CreateSubscriptionAsync(command, token);
        public Task<SubscriptionSnapshot> ChangeSubscriptionAsync(SubscriptionChange command, CancellationToken token) => inner.ChangeSubscriptionAsync(command, token);
        public Task<ImmutableArray<SubscriptionSnapshot>> ListSubscriptionsAsync(CancellationToken token) => inner.ListSubscriptionsAsync(token);
        public Task<SubscriptionSnapshot?> FindSubscriptionAsync(Guid id, CancellationToken token) => inner.FindSubscriptionAsync(id, token);
        public Task<SectionSnapshot> ReadSectionAsync(SectionKey key, CancellationToken token) => inner.ReadSectionAsync(key, token);
        public Task<CommitContext> ReadCommitContextAsync(CheckAttemptToken attempt, SectionKey key, CancellationToken token) => inner.ReadCommitContextAsync(attempt, key, token);
        public Task<ImmutableArray<PendingDelivery>> ReadPendingDeliveriesAsync(int limit, CancellationToken token) => inner.ReadPendingDeliveriesAsync(limit, token);
        public Task<DeliveryContext> ReadDeliveryContextAsync(Guid id, CancellationToken token) => inner.ReadDeliveryContextAsync(id, token);
        public Task<DeliveryWriteResult> RecordDeliveryResultAsync(DeliveryResultCommand command, CancellationToken token) => inner.RecordDeliveryResultAsync(command, token);
        public Task<LegacyImportReceipt> ImportLegacyBatchAsync(LegacyImportBatch command, CancellationToken token) => inner.ImportLegacyBatchAsync(command, token);
    }
}
