using System.Collections.Immutable;
using System.Threading.Channels;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Monitoring.Scheduling;

public sealed partial class WatchMonitorLoop
{
    private readonly IWatchStore _store;
    private readonly ICourseSource _source;
    private readonly ISourcePolicyStore _policyStore;
    private readonly TimeProvider _clock;
    private readonly WatchMonitorOptions _options;
    private readonly ChannelWriter<WatchMonitorUpdate>? _observer;
    private readonly long _origin;
    private readonly object _sync = new();
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite });
    private ImmutableArray<CancellationTokenSource> _owned = [];
    private ImmutableDictionary<SectionKey, Task<WatchRefreshResult>> _joinable = ImmutableDictionary<SectionKey, Task<WatchRefreshResult>>.Empty;
    private ImmutableDictionary<SectionKey, TaskCompletionSource<WatchRefreshResult>> _refreshes = ImmutableDictionary<SectionKey, TaskCompletionSource<WatchRefreshResult>>.Empty;
    private WatchMonitorSnapshot _snapshot;
    private bool _online = true;
    private bool _suspended;
    private bool _finished;
    private long _cancelEpoch;
    private int _started;
    private int _cancellationFailure;
    private DateTimeOffset? _nextExpiryUtc;

    public WatchMonitorLoop(IWatchStore store, ICourseSource source, ISourcePolicyStore policyStore,
        TimeProvider clock, WatchMonitorOptions options, ChannelWriter<WatchMonitorUpdate>? observer = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _policyStore = policyStore ?? throw new ArgumentNullException(nameof(policyStore));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _observer = observer;
        _origin = clock.GetTimestamp();
        _appliedProfile = new(options.Profile, options.InitialPreferencesRevision);
        var initialAcknowledgement = new TaskCompletionSource<WatchMonitorProfileSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        initialAcknowledgement.SetResult(_appliedProfile);
        _latestProfile = new(_appliedProfile, initialAcknowledgement);
        _snapshot = new(clock.GetUtcNow(), true, false, [], [], [], null, _appliedProfile);
    }

    public WatchMonitorSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public WatchMonitorProfileSnapshot AppliedRefreshProfile => Snapshot.AppliedRefreshProfile;

    /// <summary>One lifetime. Cancellation stops admission and awaits all logical operations owned by this loop.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("A monitor loop has exactly one lifetime.");
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _cancellationFailure) != 0) throw new WatchMonitorException("monitor_cancellation_failed");
                var committed = await DrainCompletedAsync().ConfigureAwait(false);
                ApplyPendingProfile();
                var controls = Controls();
                if (controls.CancelEpoch != _seenCancelEpoch)
                { _seenCancelEpoch = controls.CancelEpoch; _draining = true; CancelOwned(); }
                var watches = await _store.ListSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
                ObservePolicy(await _policyStore.ReadSourcePolicyAsync(SourcePolicyIds.SnuSugang, cancellationToken).ConfigureAwait(false));
                Reconcile(watches);
                if (_draining && _jobs.IsEmpty && _catalogJobs.IsEmpty && controls.Online && !controls.Suspended)
                { _draining = false; StaggerRecovery(); }
                ResolveRefreshes(controls);
                AdmitWork(controls, cancellationToken);
                Publish(controls, committed);
                AcknowledgeAppliedProfile();
                await WaitAsync(controls, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { throw new WatchMonitorException(error is WatchMonitorException monitor ? monitor.Code : "monitor_dependency_failed"); }
        finally { await StopOwnedAsync().ConfigureAwait(false); }
        if (Volatile.Read(ref _cancellationFailure) != 0) throw new WatchMonitorException("monitor_cancellation_failed");
    }

    /// <summary>Coalesced latest-state replan; labels alone never request another source attempt.</summary>
    public void Wake() => Signal();

    public void SetOnline(bool isOnline)
    {
        lock (_sync) { if (_online == isOnline) return; _online = isOnline; if (!isOnline) _cancelEpoch++; }
        if (!isOnline) CancelOwned();
        Signal();
    }

    public void SetSuspended(bool isSuspended)
    {
        lock (_sync) { if (_suspended == isSuspended) return; _suspended = isSuspended; if (isSuspended) _cancelEpoch++; }
        if (isSuspended) CancelOwned();
        Signal();
    }

    /// <summary>Join monitor-owned work; canceling this waiter never cancels its shared logical attempt.</summary>
    public Task<WatchRefreshResult> RefreshAsync(SectionKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        Task<WatchRefreshResult> task;
        lock (_sync)
        {
            if (_started == 0 || _finished) throw new InvalidOperationException("The monitor is not running.");
            if (!_online || _suspended || Snapshot.ReasonCode is "source_access_restricted" or "source_cooldown" or "draining")
                return Task.FromResult(new WatchRefreshResult(WatchRefreshDisposition.Deferred, reasonCode: Snapshot.ReasonCode ?? "offline"));
            if (!_joinable.TryGetValue(key, out task!))
            {
                if (!_refreshes.TryGetValue(key, out var promise))
                {
                    if (_refreshes.Count >= _options.MaxUniqueSections) throw new InvalidOperationException("The monitor refresh queue is full.");
                    promise = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _refreshes = _refreshes.Add(key, promise);
                }
                task = promise.Task;
            }
        }
        Signal();
        return task.WaitAsync(cancellationToken);
    }

    private void Signal() => _wake.Writer.TryWrite(0);
    private (bool Online, bool Suspended, long CancelEpoch) Controls()
    { lock (_sync) return (_online, _suspended, _cancelEpoch); }

    private void CancelOwned()
    {
        ImmutableArray<CancellationTokenSource> owned;
        lock (_sync) owned = _owned;
        foreach (var cancellation in owned) CancelOne(cancellation);
    }

    private void CancelOne(CancellationTokenSource cancellation)
    {
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { /* Already drained by the owner. */ }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Interlocked.Exchange(ref _cancellationFailure, 1); }
    }

    private void UpdateOwned()
    {
        lock (_sync)
        {
            _owned = _jobs.Values.Select(job => job.Cancellation).Concat(_catalogJobs.Values.Select(job => job.Cancellation)).ToImmutableArray();
            _joinable = _jobs.Values.Where(job => !job.Cancellation.IsCancellationRequested)
                .SelectMany(job => job.Plan.Scope.Select(key => (Key: key, Task: job.Completion.Task)))
                .ToImmutableDictionary(item => item.Key, item => item.Task);
        }
    }

    private async Task StopOwnedAsync()
    {
        lock (_sync) { _finished = true; RejectUnappliedProfiles(); }
        CancelOwned();
        var tasks = _jobs.Values.Where(job => job.Work is not null).Select(job => (Task)job.Work!)
            .Concat(_catalogJobs.Values.Select(job => (Task)job.Work)).ToArray();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { /* Every owned task is observed before owner release. */ }
        foreach (var job in _jobs.Values)
        {
            FinishWaiters(job, new(WatchRefreshDisposition.Deferred, job.Plan.Query, "monitor_stopped"));
            job.Cancellation.Dispose();
        }
        foreach (var job in _catalogJobs.Values) job.Cancellation.Dispose();
        lock (_sync)
        {
            foreach (var promise in _refreshes.Values) promise.TrySetResult(new(WatchRefreshDisposition.Deferred, reasonCode: "monitor_stopped"));
            _refreshes = _refreshes.Clear(); _joinable = _joinable.Clear(); _owned = [];
        }
        _wake.Writer.TryComplete();
    }

    private async Task WaitAsync((bool Online, bool Suspended, long CancelEpoch) controls, CancellationToken cancellationToken)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = NextDelay(controls);
        var signal = _wake.Reader.WaitToReadAsync(waiting.Token).AsTask();
        var timer = Task.Delay(delay is { } value ? (value > MaximumTimer ? MaximumTimer : value) : Timeout.InfiniteTimeSpan,
            _clock, waiting.Token);
        await Task.WhenAny(signal, timer).ConfigureAwait(false);
        waiting.Cancel();
        try { await Task.WhenAll(signal, timer).ConfigureAwait(false); }
        catch (OperationCanceledException) when (waiting.IsCancellationRequested) { }
        while (_wake.Reader.TryRead(out _)) { }
    }

    private TimeSpan? NextDelay((bool Online, bool Suspended, long CancelEpoch) controls)
    {
        var now = MonotonicNow;
        var due = _jobs.Values.Where(job => job.Work is null && !job.Cancellation.IsCancellationRequested)
            .Select(job => Remaining(job.CommitDue, now));
        if (AdmissionReason(controls) is null && _jobs.Count + _catalogJobs.Count < _options.MaxConcurrentQueries)
            due = due.Concat(_catalogs.Values.Where(entry => !_catalogJobs.ContainsKey(entry.Year)).Select(entry => Remaining(entry.Due, now)))
                .Concat(_queries.Where(item => !_jobs.ContainsKey(item.Key)).Select(item => Remaining(item.Value.Due, now)));
        if (AdmissionReason(controls) == "source_cooldown")
            due = due.Append(Later(Remaining(_cooldownDue, now), _policy.Cooldown is { } cooldown
                ? Remaining(cooldown.NotBeforeUtc - _clock.GetUtcNow(), TimeSpan.Zero) : TimeSpan.Zero));
        if (_nextExpiryUtc is { } expiry) due = due.Append(Remaining(expiry - _clock.GetUtcNow(), TimeSpan.Zero));
        var next = due.Select(value => (TimeSpan?)value).Min();
        return next;
    }
}
