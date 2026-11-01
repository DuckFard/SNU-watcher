using System.Collections.Immutable;
using System.Diagnostics;
using SnuWatcher.Application.Delivery;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Migration;
using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Persistence;
using SnuWatcher.Integration.Tests.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests;

public sealed class MonitoringRuntimeSessionTests
{
    [Fact]
    public async Task EmptySessionMakesNoSourceCallsAndLeavesInjectedResourcesOwnedByCaller()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        using var gate = new SemaphoreSlim(1, 1);
        var clock = new SessionClock(fixture.Clock);
        var source = new Source(fixture.Store, clock);
        var channel = new AlertChannel();
        var session = Session(fixture, source, channel, clock, gate);
        await session.StartAsync();
        await UntilAsync(() => Task.FromResult(session.LatestSnapshot is not null));

        Assert.Equal(0, source.CatalogCalls);
        Assert.Equal(0, source.SearchCalls);
        Assert.Equal(0, source.DetailCalls);
        Assert.Empty(channel.Envelopes);
        var stop = session.StopAsync();
        Assert.Same(stop, session.StopAsync());
        Assert.True((await stop.WaitAsync(Timeout)).IsFinalized);
        Assert.Equal(RuntimeSessionState.Stopped, session.State);
        Assert.Null(session.Failure);
        await session.DisposeAsync();
        Assert.Equal(0, source.DisposeCount);
        Assert.Empty(await fixture.Store.ListSubscriptionsAsync(default));
        Assert.True(gate.Wait(0));
        gate.Release();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());
    }

    [Fact]
    public async Task StartupRecoveryCompletesBeforeAnyNewSubmissionAndKeepsTheExistingBatchKey()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        await fixture.AddAsync(key);
        await fixture.ObserveAsync(key, 119);
        var old = await StoreBatchBehaviorTests.ReserveAsync(fixture, key);
        using var gate = new SemaphoreSlim(1, 1);
        var clock = new SessionClock(fixture.Clock);
        var source = new Source(fixture.Store, clock);
        var channel = new AlertChannel { Handler = async (envelope, _) =>
        {
            Assert.DoesNotContain(await fixture.Store.ReadUnresolvedAttemptsAsync(null, 100, default), row => row.AttemptId == old.AttemptId);
            var covered = Assert.Single(await fixture.Store.ReadBatchAttemptCoverageAsync(envelope.AttemptId, 0, 100, default));
            Assert.Equal(1, covered.AttemptsBefore);
            return DesktopAlertOutcome.OsAccepted;
        } };
        await using var session = Session(fixture, source, channel, clock, gate);

        await session.StartAsync();
        await UntilAsync(async () => channel.Envelopes.Length == 1 && (await fixture.Store.ReadPendingDeliveriesAsync(10, default)).IsEmpty);

        Assert.Equal(old.Envelope.NativeDedupeKey, channel.Envelopes[0].NativeDedupeKey);
        Assert.True((await session.StopAsync()).IsFinalized);
    }

    [Fact]
    public async Task RetryableNativeFailureDoesNotStopPollingOrBecomeRuntimeFatalState()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.AddAsync(StoreFixture.Key());
        using var gate = new SemaphoreSlim(1, 1);
        var clock = new SessionClock(fixture.Clock);
        var source = new Source(fixture.Store, clock);
        var channel = new AlertChannel { Handler = (_, _) => Task.FromResult(DesktopAlertOutcome.RetryableFailure) };
        await using var session = Session(fixture, source, channel, clock, gate);
        await session.StartAsync();
        await UntilAsync(() => Task.FromResult(channel.Envelopes.Length >= 1));

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True(session.Wake());
        await UntilAsync(() => Task.FromResult(source.SearchCalls >= 2));

        Assert.Equal(RuntimeSessionState.Running, session.State);
        Assert.Null(session.Failure);
        Assert.False(session.FailureTask.IsCompleted);
        Assert.True((await session.StopAsync()).IsFinalized);
    }

    [Fact]
    public async Task SlowDriverDoesNotHoldPollingAndStopCancelsMonitorBeforeDriverCompletes()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.AddAsync(StoreFixture.Key());
        using var gate = new SemaphoreSlim(1, 1);
        var clock = new SessionClock(fixture.Clock);
        var source = new Source(fixture.Store, clock);
        var searching = NewSignal();
        var sourceCanceled = NewSignal();
        var never = new TaskCompletionSource<SourceRead<CourseSearchData>>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Handler = async (query, token) =>
        {
            if (source.SearchCalls == 1) return await source.RowsAsync(query, token);
            searching.TrySetResult();
            try { return await never.Task.WaitAsync(token); }
            catch (OperationCanceledException) { sourceCanceled.TrySetResult(); throw; }
        };
        var sending = NewSignal();
        var driver = new TaskCompletionSource<DesktopAlertOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new AlertChannel { Handler = (_, _) => { sending.TrySetResult(); return driver.Task; } };
        await using var session = Session(fixture, source, channel, clock, gate);
        await session.StartAsync();
        await sending.Task.WaitAsync(Timeout);
        clock.Advance(TimeSpan.FromSeconds(31));
        session.Wake();
        await searching.Task.WaitAsync(Timeout);

        var stop = session.StopAsync();
        await sourceCanceled.Task.WaitAsync(Timeout);
        Assert.False(stop.IsCompleted);
        Assert.False(session.Wake());
        driver.SetResult(DesktopAlertOutcome.OsAccepted);
        Assert.True((await stop.WaitAsync(Timeout)).IsFinalized);
        Assert.Single(channel.Envelopes);
    }

    [Fact]
    public async Task FailedKnownResultStopsActorsRetainsResourcesAndFinalizationRetriesExactCommandWithoutNewSend()
    {
        var fault = false;
        await using var fixture = await StoreFixture.CreateAsync(stage =>
        {
            if (fault && stage == SqliteStoreWriteStage.BatchResult) throw new IOException("private result store failure");
        });
        await fixture.AddAsync(StoreFixture.Key());
        using var gate = new SemaphoreSlim(1, 1);
        var clock = new SessionClock(fixture.Clock);
        var source = new Source(fixture.Store, clock);
        var channel = new AlertChannel();
        var batches = new RecordingBatches(fixture.Store);
        var session = Session(fixture, source, channel, clock, gate, batches: batches);
        fault = true;
        await session.StartAsync();
        var failure = await session.FailureTask.WaitAsync(Timeout);
        Assert.Equal(RuntimeFailureKind.SavedState, failure.Kind);
        var blocked = await session.StopAsync().WaitAsync(Timeout);
        Assert.Equal(RuntimeSessionStopDisposition.Blocked, blocked.Disposition);
        Assert.Single(blocked.PendingKnownSections);
        Assert.Equal(RuntimeSessionState.StopBlocked, session.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.DisposeAsync().AsTask());
        var searches = source.SearchCalls;
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(session.Wake());
        Assert.Equal(searches, source.SearchCalls);
        Assert.Equal(0, source.DisposeCount);
        Assert.Equal(0, Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(10, default)).Attempts);
        fault = false;

        var firstRetry = session.RetryFinalizeStopAsync();
        var secondRetry = session.RetryFinalizeStopAsync();
        Assert.True((await firstRetry.WaitAsync(Timeout)).IsFinalized);
        Assert.True((await secondRetry.WaitAsync(Timeout)).IsFinalized);
        Assert.All(batches.Commands, command => Assert.Same(batches.Commands[0], command));
        Assert.Single(channel.Envelopes);
        Assert.Empty(await fixture.Store.ReadPendingDeliveriesAsync(10, default));
        Assert.Equal(RuntimeSessionState.Stopped, session.State);
        await session.DisposeAsync();
        Assert.True(gate.Wait(0));
        gate.Release();
    }

    [Fact]
    public async Task AdmittedMutationCanCommitLateButShutdownDrainsItAndRejectsFurtherCommands()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        using var gate = new SemaphoreSlim(1, 1);
        var clock = new SessionClock(fixture.Clock);
        var source = new Source(fixture.Store, clock);
        await using var session = Session(fixture, source, new AlertChannel(), clock, gate, options: new(maxAdmittedCommands: 1));
        await session.StartAsync();
        await UntilAsync(() => Task.FromResult(session.LatestSnapshot is not null));
        await gate.WaitAsync();
        var mutation = session.CreateAsync(fixture.NewSection(StoreFixture.Key(), initialNotice: false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CreateAsync(fixture.NewSection(StoreFixture.Key("013"))));
        var stop = session.StopAsync();
        Assert.False(stop.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CreateAsync(fixture.NewSection(StoreFixture.Key("014"))));
        gate.Release();

        var committed = await mutation.WaitAsync(Timeout);
        Assert.True((await stop.WaitAsync(Timeout)).IsFinalized);
        Assert.Equal(committed.Id, Assert.Single(await fixture.Store.ListSubscriptionsAsync(default)).Id);
        Assert.Equal(0, source.SearchCalls);
    }

    [Fact]
    public async Task SavedPolicyStorageDiagnosticIsStickyFatalEvenWhenNoUiReaderConsumesUpdates()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        using var gate = new SemaphoreSlim(1, 1);
        var clock = new SessionClock(fixture.Clock);
        var source = new Source(fixture.Store, clock);
        string? diagnostic = null;
        await using var session = Session(fixture, source, new AlertChannel(), clock, gate, diagnostic: () => Volatile.Read(ref diagnostic));
        await session.StartAsync();
        await UntilAsync(() => Task.FromResult(session.LatestSnapshot is not null));
        diagnostic = "source_policy_storage_failed";
        session.Wake();

        var failure = await session.FailureTask.WaitAsync(Timeout);

        Assert.Equal(RuntimeFailureKind.SavedState, failure.Kind);
        Assert.Equal("source_policy_storage_failed", failure.Code);
        Assert.True((await session.StopAsync()).IsFinalized);
        Assert.Equal(0, source.CatalogCalls);
        Assert.Equal(0, source.SearchCalls);
    }

    [Fact]
    public async Task BlockingDriverCancellationCallbackCannotBlockStopAdmissionOrMonitorCancellation()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.AddAsync(StoreFixture.Key());
        using var gate = new SemaphoreSlim(1, 1);
        using var releaseCallback = new ManualResetEventSlim();
        var clock = new SessionClock(fixture.Clock);
        var source = new Source(fixture.Store, clock);
        var searchStarted = NewSignal();
        var callbackStarted = NewSignal();
        var sourceCanceled = NewSignal();
        var never = new TaskCompletionSource<SourceRead<CourseSearchData>>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Handler = async (query, token) =>
        {
            if (source.SearchCalls == 1) return await source.RowsAsync(query, token);
            searchStarted.TrySetResult();
            try { return await never.Task.WaitAsync(token); }
            catch (OperationCanceledException) { sourceCanceled.TrySetResult(); throw; }
        };
        var sending = NewSignal();
        var driver = new TaskCompletionSource<DesktopAlertOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new AlertChannel { Handler = async (_, token) =>
        {
            using var registration = token.Register(() =>
            {
                callbackStarted.TrySetResult();
                if (!releaseCallback.Wait(Timeout)) throw new TimeoutException("The private cancellation callback was not released.");
            });
            sending.TrySetResult();
            return await driver.Task;
        } };
        await using var session = Session(fixture, source, channel, clock, gate);
        await session.StartAsync();
        await sending.Task.WaitAsync(Timeout);
        clock.Advance(TimeSpan.FromSeconds(31));
        session.Wake();
        await searchStarted.Task.WaitAsync(Timeout);
        var returned = NewSignal();
        var stopping = Task.Run(async () =>
        {
            var work = session.StopAsync();
            returned.TrySetResult();
            return await work;
        });
        try
        {
            await callbackStarted.Task.WaitAsync(Timeout);
            await returned.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await sourceCanceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(stopping.IsCompleted);
        }
        finally { releaseCallback.Set(); driver.TrySetResult(DesktopAlertOutcome.OsAccepted); }
        Assert.True((await stopping.WaitAsync(Timeout)).IsFinalized);
    }

    [Fact]
    public async Task StopDuringStartupWaitsActualRecoveryCommitAndStartsNoActorsOrSourceCalls()
    {
        using var release = new ManualResetEventSlim();
        var recording = NewSignal();
        await using var fixture = await StoreFixture.CreateAsync(stage =>
        {
            if (stage != SqliteStoreWriteStage.BatchResult) return;
            recording.TrySetResult();
            if (!release.Wait(Timeout)) throw new TimeoutException("The private recovery transaction was not released.");
        });
        await fixture.AddAsync(StoreFixture.Key());
        await fixture.ObserveAsync(StoreFixture.Key(), 119);
        await StoreBatchBehaviorTests.ReserveAsync(fixture, StoreFixture.Key());
        using var gate = new SemaphoreSlim(1, 1);
        var clock = new SessionClock(fixture.Clock);
        var source = new Source(fixture.Store, clock);
        var channel = new AlertChannel();
        await using var session = Session(fixture, source, channel, clock, gate);
        var starting = session.StartAsync();
        await recording.Task.WaitAsync(Timeout);
        var stopping = session.StopAsync();
        try
        {
            Assert.False(stopping.IsCompleted);
            Assert.Equal(0, source.CatalogCalls);
            Assert.Equal(0, source.SearchCalls);
            Assert.Empty(channel.Envelopes);
        }
        finally { release.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.True((await stopping.WaitAsync(Timeout)).IsFinalized);
        Assert.Null(session.Failure);
        Assert.Empty(await fixture.Store.ReadUnresolvedAttemptsAsync(null, 100, default));
        Assert.Equal(1, Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(10, default)).Attempts);
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static MonitoringRuntimeSession Session(StoreFixture f, Source source, AlertChannel channel, SessionClock clock,
        SemaphoreSlim gate, RuntimeSessionOptions? options = null, IBatchDeliveryStore? batches = null, Func<string?>? diagnostic = null) =>
        new(f.Store, f.Store, batches ?? f.Store, f.Store, f.Store, source, channel, clock,
            options ?? new(new(WatchMonitorProfile.Faster, jitterPercent: 0)), gate, diagnostic);
    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!await condition())
        {
            if (deadline.Elapsed > Timeout) throw new TimeoutException("The private runtime session did not reach the expected state.");
            await Task.Delay(5);
        }
    }

    private sealed class SessionClock(StoreClock utc) : TimeProvider
    {
        private long _extra;
        public override DateTimeOffset GetUtcNow() => utc.GetUtcNow();
        public override long GetTimestamp() => Stopwatch.GetTimestamp() + Interlocked.Read(ref _extra);
        public override long TimestampFrequency => Stopwatch.Frequency;
        internal void Advance(TimeSpan value)
        { utc.Advance(value); Interlocked.Add(ref _extra, checked((long)(value.TotalSeconds * TimestampFrequency))); }
    }

    private sealed class Source(SqliteWatchStore store, TimeProvider clock) : ICourseSource, IAsyncDisposable
    {
        private int _catalog, _search, _detail, _dispose;
        internal int CatalogCalls => Volatile.Read(ref _catalog);
        internal int SearchCalls => Volatile.Read(ref _search);
        internal int DetailCalls => Volatile.Read(ref _detail);
        internal int DisposeCount => Volatile.Read(ref _dispose);
        internal Func<CourseSearchQuery, CancellationToken, Task<SourceRead<CourseSearchData>>>? Handler { get; set; }
        public Task<SourceRead<CourseCatalogData>> GetCatalogAsync(CatalogQuery query, SourceRequestPurpose purpose, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _catalog);
            return Task.FromResult(new SourceRead<CourseCatalogData>(SourceReadStatus.Complete,
                new(query, [new CatalogTerm(query.Year, "U000200002U000300002", "Second semester", null)], []), [], clock.GetUtcNow()));
        }
        public Task<SourceRead<CourseSearchData>> SearchAsync(CourseSearchQuery query, SourceRequestPurpose purpose, CancellationToken token)
        { Interlocked.Increment(ref _search); return Handler?.Invoke(query, token) ?? RowsAsync(query, token); }
        internal async Task<SourceRead<CourseSearchData>> RowsAsync(CourseSearchQuery query, CancellationToken token)
        {
            var watches = await store.ListSubscriptionsAsync(token);
            var keys = watches.SelectMany(w => w.Members).Select(m => m.Key).Where(k => k.Year == query.Year
                && k.SemesterCode + k.DetailSemesterCode == query.TermSelector && k.CourseCode == query.CourseCode).Distinct();
            return new(SourceReadStatus.Complete, new(query, keys.Select(key => new CourseSearchRow(key,
                "Statistics", null, null, null, 119, 120, null, "Enrollment/Quota", clock.GetUtcNow())), 1), [], clock.GetUtcNow());
        }
        public Task<SourceRead<CourseDetails>> GetDetailsAsync(SectionKey key, SourceRequestPurpose purpose, CancellationToken token)
        { Interlocked.Increment(ref _detail); throw new NotSupportedException(); }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref _dispose); return ValueTask.CompletedTask; }
    }

    private sealed class AlertChannel : IBatchDesktopAlertChannel
    {
        private ImmutableArray<BatchDeliveryEnvelope> _envelopes = [];
        private readonly object _sync = new();
        internal ImmutableArray<BatchDeliveryEnvelope> Envelopes { get { lock (_sync) return _envelopes; } }
        internal Func<BatchDeliveryEnvelope, CancellationToken, Task<DesktopAlertOutcome>> Handler { get; set; } = (_, _) =>
            Task.FromResult(DesktopAlertOutcome.OsAccepted);
        public Task<DesktopAlertOutcome> SendAsync(BatchDeliveryEnvelope envelope, CancellationToken token)
        { lock (_sync) _envelopes = _envelopes.Add(envelope); return Handler(envelope, token); }
    }

    private sealed class RecordingBatches(IBatchDeliveryStore inner) : IBatchDeliveryStore
    {
        internal List<BatchAttemptResultCommand> Commands { get; } = [];
        public Task<ImmutableArray<SectionDeliveryWork>> ReadPendingDeliverySectionsAsync(int limit, CancellationToken ct) => inner.ReadPendingDeliverySectionsAsync(limit, ct);
        public Task<BatchProjection> ProjectDeliveryBatchAsync(ProjectDeliveryBatchCommand command, CancellationToken ct) => inner.ProjectDeliveryBatchAsync(command, ct);
        public Task<BatchDeliveryContext> ReadDeliveryBatchContextAsync(Guid id, BatchDeliveryPolicy policy, DateTimeOffset at, CancellationToken ct) => inner.ReadDeliveryBatchContextAsync(id, policy, at, ct);
        public Task<BatchReservationResult> TryReserveBatchAttemptAsync(PreparedBatchAttempt command, CancellationToken ct) => inner.TryReserveBatchAttemptAsync(command, ct);
        public Task<BatchAttemptReceipt> RecordBatchAttemptResultAsync(BatchAttemptResultCommand command, CancellationToken ct)
        { Commands.Add(command); return inner.RecordBatchAttemptResultAsync(command, ct); }
        public Task<ImmutableArray<BatchAttemptCoverageItem>> ReadBatchAttemptCoverageAsync(Guid id, long after, int limit, CancellationToken ct) => inner.ReadBatchAttemptCoverageAsync(id, after, limit, ct);
    }
}
