using System.Threading.Channels;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Delivery.Batches;

/// <summary>
/// One delivery actor per coordinator. The host must not call the coordinator concurrently or perform
/// unresolved-attempt recovery while this actor is live. Fresh-request callbacks must return promptly;
/// the host owns their asynchronous monitor work. Cancellation stops admission, awaits the actual
/// coordinator/channel, then retries only canonical known results with an uncancelled token.
/// </summary>
public sealed class BatchDeliveryPump
{
    private readonly IPendingDeliveryWorkReader _reader;
    private readonly BatchDeliveryCoordinator _coordinator;
    private readonly TimeProvider _clock;
    private readonly BatchDeliveryPolicy _policy;
    private readonly Action<SectionKey> _requestFresh;
    private readonly BatchDeliveryPumpOptions _options;
    private readonly Action<SectionKey?, string>? _observer;
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Queue<SectionDeliveryWork> _page = [];
    private readonly Dictionary<SectionKey, Deferred> _deferred = [];
    private PendingDeliveryWorkCursor? _after;
    private bool _scanEnded;
    private int _lifetime;
    private long _sourceGeneration;
    private long _seenSourceGeneration;
    private long _channelGeneration;
    private long _seenChannelGeneration;
    private bool _hasSubmitted;
    private long _submissionCompletedAt;
    private SectionKey? _promoted;
    private bool _promotionSeen;
    private enum DeferredKind { Reserved, Fresh, Retry, Channel }
    private sealed record Deferred(DeferredKind Kind, long StartedAt);

    public BatchDeliveryPump(IPendingDeliveryWorkReader reader, BatchDeliveryCoordinator coordinator, TimeProvider clock,
        BatchDeliveryPolicy policy, Action<SectionKey> requestFresh, BatchDeliveryPumpOptions? options = null,
        Action<SectionKey?, string>? observer = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(requestFresh);
        _reader = reader;
        _coordinator = coordinator;
        _clock = clock;
        _policy = policy;
        _requestFresh = requestFresh;
        _options = options ?? new();
        _observer = observer;
    }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _lifetime, 1, 0) != 0)
            throw new InvalidOperationException("A delivery pump has one run lifetime.");
        return RunCoreAsync(cancellationToken);
    }

    /// <summary>Returns wake admission/coalescing status, not monitoring health.</summary>
    public bool Wake(bool sourceChanged = false, bool channelChanged = false)
    {
        if (Volatile.Read(ref _lifetime) >= 2) return false;
        // A full wake channel must not lose the separate source-change evidence.
        if (sourceChanged) Interlocked.Increment(ref _sourceGeneration);
        if (channelChanged) Interlocked.Increment(ref _channelGeneration);
        return _wake.Writer.TryWrite(0);
    }

    private async Task RunCoreAsync(CancellationToken token)
    {
        using var registration = token.Register(() => Interlocked.CompareExchange(ref _lifetime, 2, 1));
        try
        {
            while (!token.IsCancellationRequested)
            {
                BeginTurn();
                if (!await TurnAsync(token).ConfigureAwait(false)) break;
                await WaitForTurnAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            Interlocked.Exchange(ref _lifetime, 2);
            _wake.Writer.TryComplete();
            await FlushKnownAsync().ConfigureAwait(false);
            Interlocked.Exchange(ref _lifetime, 3);
        }
    }

    private void BeginTurn()
    {
        while (_wake.Reader.TryRead(out _)) { }
        var generation = Interlocked.Read(ref _sourceGeneration);
        if (generation != _seenSourceGeneration)
        {
            foreach (var key in _deferred.Where(pair => pair.Value.Kind == DeferredKind.Fresh).Select(pair => pair.Key).ToArray())
                _deferred.Remove(key);
            _seenSourceGeneration = generation;
        }
        var channelGeneration = Interlocked.Read(ref _channelGeneration);
        if (channelGeneration != _seenChannelGeneration)
        {
            foreach (var key in _deferred.Where(pair => pair.Value.Kind == DeferredKind.Channel).Select(pair => pair.Key).ToArray())
                _deferred.Remove(key);
            _seenChannelGeneration = channelGeneration;
        }
        PruneDeferred();
    }

    private async Task<bool> TurnAsync(CancellationToken token)
    {
        var work = 0;
        // This bounded coordinator-owned map may contain keys absent from every database page.
        foreach (var key in await _coordinator.ReadPendingKnownSectionsAsync(token).ConfigureAwait(false))
        {
            if (work >= _options.MaxWorkPerTurn) break;
            if (IsDeferred(key)) continue;
            work++;
            var state = await ProcessAsync(key, knownOnly: true, token).ConfigureAwait(false);
            if (state == ProcessState.Stop) return false;
        }
        while (work < _options.MaxWorkPerTurn)
        {
            token.ThrowIfCancellationRequested();
            var item = await NextWorkAsync(token).ConfigureAwait(false);
            if (item is null) break;
            work++;
            if (!ReserveTracking(item.Key)) continue;
            await WaitForSubmissionFloorAsync(token).ConfigureAwait(false);
            var state = await ProcessAsync(item.Key, knownOnly: false, token).ConfigureAwait(false);
            if (state == ProcessState.Stop) return false;
            if (state == ProcessState.Yield) break; // Retained known results take priority next turn.
        }
        return true;
    }

    private async Task<SectionDeliveryWork?> NextWorkAsync(CancellationToken token)
    {
        if (_page.Count > 0) return _page.Dequeue();
        if (_scanEnded)
        {
            // One bounded promotion survives long scans, but not a complete scan in which its work
            // disappeared. An early deferred key cannot reclaim every expired tracking slot.
            if (_promoted is not null && !_promotionSeen) _promoted = null;
            _promotionSeen = false;
            _after = null;
            _scanEnded = false;
            return null; // Wrap on the next bounded turn, never spin on one stale first page.
        }
        try
        {
            var page = await _reader.ReadPageAsync(_after, _options.PageSize, token).ConfigureAwait(false);
            _after = page.NextCursor;
            _scanEnded = !page.HasMore;
            foreach (var item in page.Items) _page.Enqueue(item);
            return _page.Count == 0 ? null : _page.Dequeue();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { Notify(null, "delivery_work_unavailable"); return null; }
    }

    private enum ProcessState { Continue, Yield, Stop }
    private async Task<ProcessState> ProcessAsync(SectionKey key, bool knownOnly, CancellationToken token)
    {
        BatchDeliveryRunResult result;
        try
        {
            result = await _coordinator.TryDeliverSectionAsync(key, _policy, _options.PageSize,
                _options.MaxFoldPages, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            Defer(key, DeferredKind.Retry);
            Notify(key, "delivery_work_failed");
            return ProcessState.Continue;
        }
        if (result.SubmittedThisCall)
        {
            _submissionCompletedAt = _clock.GetTimestamp();
            _hasSubmitted = true;
        }
        return ApplyResult(key, result, knownOnly);
    }

    private ProcessState ApplyResult(SectionKey key, BatchDeliveryRunResult result, bool knownOnly)
    {
        switch (result.Disposition)
        {
            case BatchDeliveryRunDisposition.ChannelDeferred:
                Defer(key, DeferredKind.Channel);
                Notify(key, result.ReasonCode);
                return ProcessState.Continue;
            case BatchDeliveryRunDisposition.NeedsRecovery:
                Notify(key, "recovery_required");
                return ProcessState.Stop;
            case BatchDeliveryRunDisposition.NeedsFreshCheck:
                Defer(key, DeferredKind.Fresh);
                try { _requestFresh(key); }
                catch (Exception) { Notify(key, "fresh_request_failed"); }
                return ProcessState.Continue;
            case BatchDeliveryRunDisposition.ResultNotRecorded:
                Defer(key, DeferredKind.Retry);
                Notify(key, "known_result_not_recorded");
                return ProcessState.Yield;
            case BatchDeliveryRunDisposition.ResultRecorded:
                if (knownOnly) Notify(key, "known_result_recorded");
                if (result.Receipt?.PendingCount > 0)
                    Defer(key, result.KnownResult?.DiagnosticCode is "not_submitted_channel_not_ready" or "not_submitted_payload_rejected"
                        ? DeferredKind.Channel : DeferredKind.Retry);
                else _deferred.Remove(key);
                return ProcessState.Continue;
            case BatchDeliveryRunDisposition.PendingResultLimit:
                Defer(key, DeferredKind.Retry);
                Notify(key, "known_result_limit");
                return ProcessState.Yield;
            case BatchDeliveryRunDisposition.NeedsRevalidation:
            case BatchDeliveryRunDisposition.RecipientLimitReached:
                Defer(key, DeferredKind.Retry);
                Notify(key, result.Disposition == BatchDeliveryRunDisposition.RecipientLimitReached ? "recipient_limit" : "delivery_revalidation_needed");
                return ProcessState.Continue;
            default:
                _deferred.Remove(key);
                return ProcessState.Continue;
        }
    }

    private bool ReserveTracking(SectionKey key)
    {
        PruneDeferred();
        if (_promoted == key) _promotionSeen = true;
        if (IsDeferred(key)) return false;
        if (!_deferred.ContainsKey(key) && _promoted is not null && _promoted != key) return false;
        if (!_deferred.ContainsKey(key) && _deferred.Count >= _options.MaxTrackedSections)
        {
            _promoted ??= key;
            if (_promoted == key) _promotionSeen = true;
            return false;
        }
        if (_promoted == key) { _promoted = null; _promotionSeen = false; }
        _deferred[key] = new(DeferredKind.Reserved, _clock.GetTimestamp());
        return true;
    }

    private bool IsDeferred(SectionKey key)
    {
        if (!_deferred.TryGetValue(key, out var state)) return false;
        if (state.Kind == DeferredKind.Reserved || Remaining(state) <= TimeSpan.Zero)
        { _deferred.Remove(key); return false; }
        return true;
    }

    private void Defer(SectionKey key, DeferredKind kind)
    {
        PruneDeferred();
        if (_deferred.ContainsKey(key) || _deferred.Count < _options.MaxTrackedSections)
            _deferred[key] = new(kind, _clock.GetTimestamp());
    }

    private void PruneDeferred()
    {
        foreach (var key in _deferred.Where(pair => pair.Value.Kind != DeferredKind.Reserved && Remaining(pair.Value) <= TimeSpan.Zero)
            .Select(pair => pair.Key).ToArray()) _deferred.Remove(key);
    }

    private TimeSpan Remaining(Deferred state)
    {
        var duration = state.Kind switch
        {
            DeferredKind.Fresh => _options.FreshRequestInterval,
            DeferredKind.Channel => _options.ChannelRecheckInterval,
            _ => _options.RetryDelay,
        };
        return duration - _clock.GetElapsedTime(state.StartedAt, _clock.GetTimestamp());
    }

    private async Task WaitForSubmissionFloorAsync(CancellationToken token)
    {
        if (!_hasSubmitted) return;
        var remaining = _options.MinimumSubmissionInterval - _clock.GetElapsedTime(_submissionCompletedAt, _clock.GetTimestamp());
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, _clock, token).ConfigureAwait(false);
    }

    private async Task WaitForTurnAsync(CancellationToken token)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token);
        var wake = _wake.Reader.WaitToReadAsync(waiting.Token).AsTask();
        var timer = Task.Delay(_options.IdleInterval, _clock, waiting.Token);
        // Only race a bounded wake and timer, never a source, coordinator or OS operation.
        await Task.WhenAny(wake, timer).ConfigureAwait(false);
        await waiting.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(wake, timer).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        token.ThrowIfCancellationRequested();
    }

    private async Task FlushKnownAsync()
    {
        try
        {
            for (var pass = 0; pass < _options.ShutdownFlushAttempts; pass++)
            {
                var keys = await _coordinator.ReadPendingKnownSectionsAsync(CancellationToken.None).ConfigureAwait(false);
                if (keys.IsEmpty) return;
                foreach (var key in keys)
                {
                    // This actor is quiesced and exclusively owns the coordinator. Every key is still
                    // a known command, so this path cannot allocate a new OS submission.
                    var result = await _coordinator.TryDeliverSectionAsync(key, _policy, _options.PageSize,
                        _options.MaxFoldPages, CancellationToken.None).ConfigureAwait(false);
                    Notify(key, result.Disposition == BatchDeliveryRunDisposition.ResultRecorded ? "known_result_recorded" : "known_result_not_recorded");
                }
            }
            foreach (var key in await _coordinator.ReadPendingKnownSectionsAsync(CancellationToken.None).ConfigureAwait(false))
                Notify(key, "shutdown_results_pending");
        }
        catch (Exception) { Notify(null, "shutdown_results_pending"); }
    }

    private void Notify(SectionKey? key, string code)
    {
        try { _observer?.Invoke(key, code); }
        catch (Exception) { }
    }
}
