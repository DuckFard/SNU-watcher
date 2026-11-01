using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using SnuWatcher.Application.Migration;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Tests.Monitoring.Scheduling;

/// <summary>Moves monotonic time and timers together while allowing independent UTC corrections.</summary>
internal sealed class SplitMonitorClock(DateTimeOffset? start = null) : TimeProvider
{
    private readonly FakeTimeProvider _inner = new(start ?? MonitorFixture.Now);
    private long _utcShiftTicks;
    private Action<TimeSpan>? _beforeTimerCreated;
    private Action<TimeSpan>? _afterTimerArmed;
    private int _undisposedTimerCount;

    public override long TimestampFrequency => _inner.TimestampFrequency;
    public override TimeZoneInfo LocalTimeZone => _inner.LocalTimeZone;
    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow().AddTicks(Interlocked.Read(ref _utcShiftTicks));
    public override long GetTimestamp() => _inner.GetTimestamp();
    /// <summary>Optional phase barrier before the underlying clock anchors a newly created timer.</summary>
    public Action<TimeSpan>? BeforeTimerCreated
    {
        get => Volatile.Read(ref _beforeTimerCreated);
        set => Volatile.Write(ref _beforeTimerCreated, value);
    }
    /// <summary>Reports a successfully armed finite timer, including an explicit Change after creation.</summary>
    public Action<TimeSpan>? AfterTimerArmed
    {
        get => Volatile.Read(ref _afterTimerArmed);
        set => Volatile.Write(ref _afterTimerArmed, value);
    }
    /// <summary>Counts owned timer objects until their actual disposal completes.</summary>
    public int UndisposedTimerCount => Volatile.Read(ref _undisposedTimerCount);
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        BeforeTimerCreated?.Invoke(dueTime);
        var inner = _inner.CreateTimer(callback, state, dueTime, period);
        Interlocked.Increment(ref _undisposedTimerCount);
        var timer = new ObservedTimer(inner, this);
        try { ObserveArmed(dueTime); return timer; }
        catch { timer.Dispose(); throw; }
    }
    private void ObserveArmed(TimeSpan dueTime) { if (dueTime >= TimeSpan.Zero) AfterTimerArmed?.Invoke(dueTime); }
    public void Advance(TimeSpan value) => _inner.Advance(value);
    public void ShiftUtc(TimeSpan value) => Interlocked.Add(ref _utcShiftTicks, value.Ticks);

    private sealed class ObservedTimer(ITimer inner, SplitMonitorClock owner) : ITimer
    {
        private int _disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            var changed = inner.Change(dueTime, period);
            if (changed) owner.ObserveArmed(dueTime);
            return changed;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { inner.Dispose(); }
            finally { Interlocked.Decrement(ref owner._undisposedTimerCount); }
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { await inner.DisposeAsync().ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref owner._undisposedTimerCount); }
        }
    }
}

/// <summary>A deliberately narrow store fake: real immutable contracts/effects, without SQL or delivery.</summary>
internal sealed class FakeMonitorStore : IWatchStore
{
    private readonly object _sync = new();
    private ImmutableArray<SubscriptionSnapshot> _watches = [];
    private ImmutableArray<IssuedCheck> _issues = [];
    private ImmutableArray<PreparedSectionCommit> _commitCommands = [];
    private ImmutableDictionary<SectionKey, SectionSnapshot> _sections = ImmutableDictionary<SectionKey, SectionSnapshot>.Empty;
    private ImmutableDictionary<(CheckAttemptToken Token, SectionKey Key), (SectionCompletion Facts, SectionCommitReceipt Receipt)> _receipts =
        ImmutableDictionary<(CheckAttemptToken, SectionKey), (SectionCompletion, SectionCommitReceipt)>.Empty;
    private long _lastOrdinal;
    private long _catalogRevision;
    private int _conflictWritesRemaining;
    private int _readCount;
    private Exception? _issueFailure;
    private Exception? _listFailure;
    private Exception? _commitFailure;
    private Func<CancellationToken, Task<ImmutableArray<SubscriptionSnapshot>>>? _listHandler;

    public FakeMonitorStore(IEnumerable<SubscriptionSnapshot>? watches = null) => Watches = (watches ?? []).ToImmutableArray();
    public ImmutableArray<SubscriptionSnapshot> Watches
    {
        get { lock (_sync) return _watches; }
        set
        {
            if (value.IsDefault || value.Any(watch => watch is null)) throw new ArgumentException("Initialized watches are required.");
            lock (_sync) { _watches = value; _catalogRevision++; }
        }
    }
    public ImmutableArray<IssuedCheck> Issues { get { lock (_sync) return _issues; } }
    public ImmutableArray<PreparedSectionCommit> CommitCommands { get { lock (_sync) return _commitCommands; } }
    public ImmutableDictionary<SectionKey, SectionSnapshot> SectionSnapshots
    {
        get { lock (_sync) return _sections; }
        set { ArgumentNullException.ThrowIfNull(value); lock (_sync) _sections = value; }
    }
    public int ConflictWritesRemaining
    {
        get { lock (_sync) return _conflictWritesRemaining; }
        set { ArgumentOutOfRangeException.ThrowIfNegative(value); lock (_sync) _conflictWritesRemaining = value; }
    }
    /// <summary>Counts subscription-list reads so tests can detect an idle busy loop.</summary>
    public int ReadCount { get { lock (_sync) return _readCount; } }
    public Exception? IssueFailure { get { lock (_sync) return _issueFailure; } set { lock (_sync) _issueFailure = value; } }
    public Exception? ListFailure { get { lock (_sync) return _listFailure; } set { lock (_sync) _listFailure = value; } }
    public Exception? CommitFailure { get { lock (_sync) return _commitFailure; } set { lock (_sync) _commitFailure = value; } }
    public Func<CancellationToken, Task<ImmutableArray<SubscriptionSnapshot>>>? ListHandler
    {
        get { lock (_sync) return _listHandler; }
        set { lock (_sync) _listHandler = value; }
    }

    public Task<IssuedCheck> IssueCheckAsync(CheckIssue issue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_issueFailure is { } failure) throw failure;
            var watermark = _watches.SelectMany(watch => watch.Members)
                .Select(member => Math.Max(member.ActivationWatermark, member.InitializedByAttemptOrdinal ?? 0)).DefaultIfEmpty().Max();
            var completed = _sections.Values.Select(section => section.LatestCompletedOrdinal).DefaultIfEmpty().Max();
            _lastOrdinal = checked(Math.Max(_lastOrdinal, Math.Max(watermark, completed)) + 1);
            var issued = new IssuedCheck(new(Guid.NewGuid(), _lastOrdinal), issue.IssuedAtUtc, issue.Scope);
            _issues = _issues.Add(issued);
            return Task.FromResult(issued);
        }
    }

    public Task<ImmutableArray<SubscriptionSnapshot>> ListSubscriptionsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Func<CancellationToken, Task<ImmutableArray<SubscriptionSnapshot>>>? handler;
        ImmutableArray<SubscriptionSnapshot> watches;
        lock (_sync)
        {
            _readCount++;
            if (_listFailure is { } failure) throw failure;
            handler = _listHandler;
            watches = _watches;
        }
        return handler?.Invoke(cancellationToken) ?? Task.FromResult(watches);
    }

    public Task<SubscriptionSnapshot?> FindSubscriptionAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync) return Task.FromResult(_watches.SingleOrDefault(watch => watch.Id == id));
    }

    public Task<SectionSnapshot> ReadSectionAsync(SectionKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync) return Task.FromResult(Section(key));
    }

    public Task<CommitContext> ReadCommitContextAsync(CheckAttemptToken token, SectionKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync) return Task.FromResult(Context(token, key));
    }

    public Task<TryCommitResult> TryCommitSectionAsync(PreparedSectionCommit command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _commitCommands = _commitCommands.Add(command);
            if (_commitFailure is { } failure) throw failure;
            var completion = command.Completion;
            _ = Context(completion.Token, completion.Key);
            var replayKey = (completion.Token, completion.Key);
            if (_receipts.TryGetValue(replayKey, out var replay))
            {
                if (replay.Facts != completion) throw new ObservationReplayConflictException();
                return Task.FromResult(TryCommitResult.Replayed(replay.Receipt));
            }
            if (_conflictWritesRemaining > 0)
            {
                _conflictWritesRemaining--;
                return Task.FromResult(TryCommitResult.VersionConflict());
            }
            var previous = Section(completion.Key);
            if (command.ExpectedCatalogRevision != _catalogRevision || command.ExpectedSectionVersion != previous.Version)
                return Task.FromResult(TryCommitResult.VersionConflict());
            var stale = completion.Token.Ordinal <= previous.LatestCompletedOrdinal;
            if (!stale) Apply(command, previous);
            var receipt = new SectionCommitReceipt(completion.Token, completion.Key,
                stale ? CommitDisposition.IgnoredStale : Disposition(command.Effects.EvaluationOutcome),
                Section(completion.Key).Version, !stale && command.Effects.Notification is not null ? Guid.NewGuid() : null);
            _receipts = _receipts.Add(replayKey, (completion, receipt));
            return Task.FromResult(TryCommitResult.Committed(receipt));
        }
    }

    private CommitContext Context(CheckAttemptToken token, SectionKey key) => new(
        _issues.Single(issued => issued.Token == token), Section(key), _catalogRevision,
        _watches.Where(watch => watch.Members.Any(member => member.Key == key)));
    private SectionSnapshot Section(SectionKey key) => _sections.TryGetValue(key, out var section) ? section : SectionSnapshot.Unchecked(key);

    private void Apply(PreparedSectionCommit command, SectionSnapshot previous)
    {
        var completion = command.Completion;
        var baseline = command.Effects.NextBaseline ?? previous.Baseline;
        _sections = _sections.SetItem(completion.Key, new(completion.Key, checked(previous.Version + 1), completion.Token.Ordinal,
            command.Effects.HealthOutcome, completion.ObservedAtUtc, baseline?.ObservedAt, baseline));
        if (command.Effects.Initializations.IsEmpty) return;
        var initialized = command.Effects.Initializations.ToDictionary(initialization => initialization.SubscriptionId);
        _watches = _watches.Select(watch => !initialized.TryGetValue(watch.Id, out var initialization)
                || initialization.Generation != watch.ActivationGeneration ? watch
                : new SubscriptionSnapshot(watch.Id, watch.DefinitionRevision, watch.ActivationGeneration, watch.Kind,
                    watch.Label, watch.Status, watch.InitialNoticeEnabled, watch.Expiry, watch.CreatedAtUtc,
                    watch.Members.Select(member => member.Key == completion.Key
                        ? new MemberSnapshot(member.Key, member.ActivationWatermark, completion.Token.Ordinal) : member)))
            .ToImmutableArray();
    }

    private static CommitDisposition Disposition(EvaluationOutcome outcome) => outcome switch
    {
        EvaluationOutcome.BaselineEstablished or EvaluationOutcome.Opening or EvaluationOutcome.NoOpening => CommitDisposition.AcceptedValid,
        EvaluationOutcome.IgnoredUnknown => CommitDisposition.AcceptedUnknown,
        EvaluationOutcome.RevalidationRequired => CommitDisposition.RevalidationRequired,
        _ => throw new InvalidOperationException("Unexpected fixture commit effects."),
    };

    public Task<SubscriptionSnapshot> CreateSubscriptionAsync(NewSubscription command, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SubscriptionSnapshot> ChangeSubscriptionAsync(SubscriptionChange command, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<ImmutableArray<PendingDelivery>> ReadPendingDeliveriesAsync(int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<DeliveryContext> ReadDeliveryContextAsync(Guid eventId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<DeliveryWriteResult> RecordDeliveryResultAsync(DeliveryResultCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<LegacyImportReceipt> ImportLegacyBatchAsync(LegacyImportBatch command, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class FakeMonitorPolicy : ISourcePolicyStore
{
    private readonly object _sync = new();
    private SourcePolicySnapshot _snapshot = SourcePolicySnapshot.Initial(SourcePolicyIds.SnuSugang);
    public SourcePolicySnapshot Snapshot
    {
        get { lock (_sync) return _snapshot; }
        set { ArgumentNullException.ThrowIfNull(value); lock (_sync) _snapshot = value; }
    }
    public Task<SourcePolicySnapshot> ReadSourcePolicyAsync(string sourceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (sourceId != _snapshot.SourceId) throw new ArgumentException("Unexpected fixture source identity.", nameof(sourceId));
            return Task.FromResult(_snapshot);
        }
    }
    public Task<SourceRequestIssueResult> TryIssueSourceRequestAsync(SourceRequestIssue command, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SourcePolicyReceipt> RecordSourceRequestResultAsync(SourceRequestResultCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SourceAccessResumeResult> ResumeSourceAccessAsync(SourceAccessResumeCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed record MonitorSourceCall<T>(T Query, SourceRequestPurpose Purpose, DateTimeOffset StartedAtUtc, long StartedTimestamp);

/// <summary>Tracks the actual handler lifetime even when a handler deliberately ignores cancellation.</summary>
internal sealed class FakeMonitorSource(FakeMonitorStore store, TimeProvider clock) : ICourseSource
{
    private readonly object _sync = new();
    private ImmutableArray<MonitorSourceCall<CatalogQuery>> _catalogCalls = [];
    private ImmutableArray<MonitorSourceCall<CourseSearchQuery>> _searchCalls = [];
    private Func<CatalogQuery, CancellationToken, Task<SourceRead<CourseCatalogData>>>? _catalogHandler;
    private Func<CourseSearchQuery, CancellationToken, Task<SourceRead<CourseSearchData>>>? _searchHandler;
    private int _active;
    private int _maxActive;
    public Func<CatalogQuery, CancellationToken, Task<SourceRead<CourseCatalogData>>>? CatalogHandler
    {
        get { lock (_sync) return _catalogHandler; }
        set { lock (_sync) _catalogHandler = value; }
    }
    public Func<CourseSearchQuery, CancellationToken, Task<SourceRead<CourseSearchData>>>? SearchHandler
    {
        get { lock (_sync) return _searchHandler; }
        set { lock (_sync) _searchHandler = value; }
    }
    public ImmutableArray<MonitorSourceCall<CatalogQuery>> CatalogCalls { get { lock (_sync) return _catalogCalls; } }
    public ImmutableArray<MonitorSourceCall<CourseSearchQuery>> SearchCalls { get { lock (_sync) return _searchCalls; } }
    public int Active { get { lock (_sync) return _active; } }
    public int MaxActive { get { lock (_sync) return _maxActive; } }

    public Task<SourceRead<CourseCatalogData>> GetCatalogAsync(CatalogQuery query, SourceRequestPurpose purpose, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Func<CatalogQuery, CancellationToken, Task<SourceRead<CourseCatalogData>>>? handler;
        lock (_sync)
        {
            _catalogCalls = _catalogCalls.Add(new(query, purpose, clock.GetUtcNow(), clock.GetTimestamp()));
            handler = _catalogHandler;
        }
        return handler?.Invoke(query, cancellationToken) ?? Task.FromResult(new SourceRead<CourseCatalogData>(SourceReadStatus.Complete,
            new(query, [new(query.Year, "21", "Second semester", null)], []), [], clock.GetUtcNow()));
    }

    public async Task<SourceRead<CourseSearchData>> SearchAsync(CourseSearchQuery query, SourceRequestPurpose purpose, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Func<CourseSearchQuery, CancellationToken, Task<SourceRead<CourseSearchData>>>? handler;
        lock (_sync)
        {
            _searchCalls = _searchCalls.Add(new(query, purpose, clock.GetUtcNow(), clock.GetTimestamp()));
            _active++;
            _maxActive = Math.Max(_maxActive, _active);
            handler = _searchHandler;
        }
        try
        {
            return handler is not null ? await handler(query, cancellationToken).ConfigureAwait(false)
                : MonitorFixture.Rows(query, store.Watches.SelectMany(watch => watch.Members.Select(member => member.Key)), clock.GetUtcNow());
        }
        finally { lock (_sync) _active--; }
    }

    public Task<SourceRead<CourseDetails>> GetDetailsAsync(SectionKey key, SourceRequestPurpose purpose, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal static class MonitorFixture
{
    internal static readonly DateTimeOffset Now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    internal static SectionKey Key(int year = 2026, string semester = "2", string detail = "1", string course = "F32.102",
        string section = "012", string subsubject = "000") => new(year, semester, detail, course, section, subsubject);
    internal static SubscriptionSnapshot Watch(IEnumerable<SectionKey> keys, Guid? id = null, long generation = 1,
        long activationWatermark = 0, long? initializedByAttemptOrdinal = null, WatchStatus status = WatchStatus.Active,
        bool initialNoticeEnabled = true, WatchExpiry? expiry = null, string label = "A watch", long definitionRevision = 1)
    {
        var members = keys.Select(key => new MemberSnapshot(key, activationWatermark, initializedByAttemptOrdinal)).ToImmutableArray();
        return new(id ?? Guid.NewGuid(), definitionRevision, generation, members.Length == 1 ? SubscriptionKind.Section : SubscriptionKind.Group,
            label, status, initialNoticeEnabled, expiry, Now, members);
    }
    internal static SourceRead<CourseSearchData> Rows(CourseSearchQuery query, IEnumerable<SectionKey> keys,
        DateTimeOffset observedAtUtc, int registered = 40, int capacity = 40) => new(SourceReadStatus.Complete,
        new(query, keys.Where(key => key.Year == query.Year && key.CourseCode == query.CourseCode
                && key.SemesterCode + key.DetailSemesterCode == query.TermSelector).Distinct()
            .Select(key => new CourseSearchRow(key, "Fixture course", null, null, null, registered, capacity, registered,
                "Enrollment/Quota", observedAtUtc)), 1), [], observedAtUtc);
    internal static async Task EventuallyAsync(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed >= TimeSpan.FromSeconds(5)) throw new TimeoutException("The monitor condition did not become true within five seconds.");
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
