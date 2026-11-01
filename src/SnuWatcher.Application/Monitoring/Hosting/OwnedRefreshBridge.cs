using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Core;
using System.Collections.Immutable;
using System.Threading.Channels;

namespace SnuWatcher.Application.Monitoring.Hosting;

/// <summary>
/// One bounded refresh actor. Four owned workers join monitor-owned work; canceling a public waiter
/// only detaches that waiter. Closing cancels bridge joins and waits for their actual completion.
/// </summary>
public sealed class OwnedRefreshBridge
{
    public const int WorkerCount = 4;
    public const int MaxDistinctKeys = 10_000;
    private readonly Func<SectionKey, CancellationToken, Task<WatchRefreshResult>> _refresh;
    private readonly int _capacity;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _closingFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<Entry> _queue;
    private ImmutableDictionary<SectionKey, Entry> _entries = ImmutableDictionary<SectionKey, Entry>.Empty;
    private Task[] _workers = [];
    private Task? _running;
    private OwnedRefreshResult? _lastResult;
    private string? _failureCode;
    private long _rejected;
    private int _lifetime;
    private bool _closed;

    public OwnedRefreshBridge(Func<SectionKey, CancellationToken, Task<WatchRefreshResult>> refresh, int maxDistinctKeys = 2000)
    {
        ArgumentNullException.ThrowIfNull(refresh);
        if (maxDistinctKeys is < 1 or > MaxDistinctKeys) throw new ArgumentOutOfRangeException(nameof(maxDistinctKeys));
        _refresh = refresh;
        _capacity = maxDistinctKeys;
        _queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(maxDistinctKeys)
            { SingleReader = false, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    }

    public int PendingCount { get { lock (_sync) return _entries.Count; } }
    /// <summary>Requests rejected because the distinct queued/running-key bound was reached.</summary>
    public long RejectedCount { get { lock (_sync) return _rejected; } }
    public OwnedRefreshResult? LastResult => Volatile.Read(ref _lastResult);

    public Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _lifetime, 1, 0) != 0)
            throw new InvalidOperationException("The owned refresh bridge has one run lifetime.");
        _running = RunCoreAsync(cancellationToken);
        return _running;
    }

    private async Task RunCoreAsync(CancellationToken token)
    {
        using var registration = token.Register(CloseAdmissionAndCancelWaiters);
        _workers = Enumerable.Range(0, WorkerCount).Select(_ => WorkerAsync()).ToArray();
        try { await Task.WhenAll(_workers).ConfigureAwait(false); }
        finally
        {
            CloseAdmissionAndCancelWaiters();
            await _closingFinished.Task.ConfigureAwait(false);
            lock (_sync) _entries = _entries.Clear();
            Interlocked.Exchange(ref _lifetime, 3);
            _stop.Dispose();
        }
        if (_failureCode is not null) throw new InvalidOperationException(_failureCode);
    }

    public bool TryRequest(SectionKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Admit(key, out _, out _);
    }

    public Task<OwnedRefreshResult> RequestAsync(SectionKey key, CancellationToken waiterCancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        waiterCancellationToken.ThrowIfCancellationRequested();
        return Admit(key, out var entry, out var rejected)
            ? entry!.Completion.Task.WaitAsync(waiterCancellationToken) : Task.FromResult(rejected!);
    }

    private bool Admit(SectionKey key, out Entry? entry, out OwnedRefreshResult? rejected)
    {
        lock (_sync)
        {
            rejected = null;
            entry = null;
            if (_closed) { rejected = Last(key, OwnedRefreshDisposition.Stopped, "refresh_stopped"); return false; }
            if (Volatile.Read(ref _lifetime) == 0)
            { rejected = Last(key, OwnedRefreshDisposition.Rejected, "refresh_not_running"); return false; }
            if (_entries.TryGetValue(key, out entry)) return true;
            if (_entries.Count >= _capacity)
            {
                if (_rejected < long.MaxValue) _rejected++;
                rejected = Last(key, OwnedRefreshDisposition.Rejected, "refresh_capacity");
                return false;
            }
            entry = new(key);
            _entries = _entries.Add(key, entry);
            // The dictionary bounds running plus queued entries, so the key channel has a slot.
            if (_queue.Writer.TryWrite(entry)) return true;
            _entries = _entries.Remove(key);
            rejected = Last(key, OwnedRefreshDisposition.Stopped, "refresh_stopped");
            return false;
        }
    }

    public void CloseAdmissionAndCancelWaiters()
    {
        bool beforeRun;
        lock (_sync)
        {
            if (_closed) return;
            _closed = true;
            beforeRun = Interlocked.CompareExchange(ref _lifetime, 2, 0) == 0;
            _queue.Writer.TryComplete();
            foreach (var entry in _entries.Values)
                entry.Completion.TrySetResult(Last(entry.Key,
                    _failureCode is null ? OwnedRefreshDisposition.Stopped : OwnedRefreshDisposition.Failed,
                    _failureCode ?? "refresh_stopped"));
        }
        try { _stop.Cancel(); }
        catch (Exception) { Interlocked.CompareExchange(ref _failureCode, "refresh_cancellation_failed", null); }
        finally
        {
            _closingFinished.TrySetResult();
            if (beforeRun) _stop.Dispose();
        }
    }

    private async Task WorkerAsync()
    {
        try
        {
            await foreach (var entry in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                _stop.Token.ThrowIfCancellationRequested();
                try
                {
                    Task<WatchRefreshResult> work;
                    lock (_sync)
                    {
                        if (_closed) return;
                        // Invocation and admission closing share one linearization point. The
                        // actual monitor join is prompt; its asynchronous work is awaited below.
                        work = _refresh(entry.Key, _stop.Token);
                    }
                    var result = await work.ConfigureAwait(false);
                    if (result is null) throw new InvalidOperationException();
                    Finish(entry, new(entry.Key, OwnedRefreshDisposition.Completed, result,
                        result.ReasonCode ?? result.Disposition switch
                        {
                            WatchRefreshDisposition.NotWatched => "not_watched",
                            WatchRefreshDisposition.Blocked => "refresh_blocked",
                            WatchRefreshDisposition.Deferred => "refresh_deferred",
                            _ => "refresh_completed",
                        }));
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                { Finish(entry, new(entry.Key, OwnedRefreshDisposition.Stopped, null, "refresh_stopped")); }
                catch (Exception)
                {
                    Interlocked.CompareExchange(ref _failureCode, "refresh_dependency_failed", null);
                    Finish(entry, new(entry.Key, OwnedRefreshDisposition.Failed, null, "refresh_dependency_failed"));
                    CloseAdmissionAndCancelWaiters();
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void Finish(Entry entry, OwnedRefreshResult result)
    {
        lock (_sync)
        {
            if (entry.Completion.TrySetResult(result)) _lastResult = result;
            _entries = _entries.Remove(entry.Key);
        }
    }

    private OwnedRefreshResult Last(SectionKey key, OwnedRefreshDisposition disposition, string code) =>
        _lastResult = new(key, disposition, null, code);

    private sealed class Entry(SectionKey key)
    {
        internal SectionKey Key { get; } = key;
        internal TaskCompletionSource<OwnedRefreshResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
