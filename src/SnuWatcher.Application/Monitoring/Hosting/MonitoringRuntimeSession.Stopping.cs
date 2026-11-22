using System.Collections.Immutable;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Monitoring.Hosting;

public sealed partial class MonitoringRuntimeSession
{
    /// <summary>One stored stop attempt. A later explicit finalization retry returns its own updated result.</summary>
    public Task<RuntimeSessionStopResult> StopAsync()
    {
        lock (_sync) return _stopPublic ??= AwaitStoppedAsync(EnsureStopCoreLocked());
    }

    private Task<RuntimeSessionStopResult> EnsureStopCoreLocked()
    {
        if (_stopCore is not null) return _stopCore;
        _stopping = true;
        if (_state != RuntimeSessionState.Stopped) _state = RuntimeSessionState.Stopping;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _stopCore = StopCoreAsync(ready.Task);
        // The stop task is retained before cancellation can synchronously finish an actor/supervisor.
        _cancellations = [RequestCancellation(_monitorStop), RequestCancellation(_pumpStop),
            RequestCancellation(_refreshStop), RequestCancellation(_refreshWaiters), RequestCancellation(_startupStop)];
        if (!_actors.ContainsKey("refresh")) _refresh.CloseAdmissionAndCancelWaiters();
        ready.SetResult();
        return _stopCore;
    }

    private async Task<RuntimeSessionStopResult> AwaitStoppedAsync(Task<RuntimeSessionStopResult> core)
    {
        var result = await core.ConfigureAwait(false);
        Task? supervisor;
        lock (_sync) supervisor = _supervisor;
        if (supervisor is not null) await supervisor.ConfigureAwait(false);
        return result;
    }

    private async Task<RuntimeSessionStopResult> StopCoreAsync(Task ready)
    {
        await ready.ConfigureAwait(false);
        foreach (var cancellation in _cancellations)
            await ObserveAsync(cancellation, "runtime_cancellation_failed").ConfigureAwait(false);
        Task? startup;
        lock (_sync) startup = _startTask;
        if (startup is not null) await ObserveAsync(startup, "runtime_startup_failed").ConfigureAwait(false);
        if (_actors.TryGetValue("delivery", out var pump)) await ObserveAsync(pump, "delivery_actor_failed").ConfigureAwait(false);
        var result = await FinalizeKnownAsync().ConfigureAwait(false);
        foreach (var pair in _actors.Where(pair => pair.Key is "monitor" or "refresh"))
            await ObserveAsync(pair.Value, pair.Key == "monitor" ? "monitor_failed" : "refresh_actor_failed").ConfigureAwait(false);
        Task[] commands;
        lock (_sync) commands = _commands.Values.ToArray();
        foreach (var command in commands) await ObserveAsync(command, "runtime_command_failed").ConfigureAwait(false);
        _hub.Complete();
        if (_actors.TryGetValue("updates", out var updates)) await ObserveAsync(updates, "runtime_update_failed").ConfigureAwait(false);
        lock (_sync)
        {
            _commands = _commands.Clear();
            _lastStop = new(result.Disposition, result.PendingKnownSections, _hub.Failure);
            _state = result.IsFinalized ? RuntimeSessionState.Stopped : RuntimeSessionState.StopBlocked;
            return _lastStop;
        }
    }

    private async Task<RuntimeSessionStopResult> FinalizeKnownAsync()
    {
        await _finalization.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var keys = await _coordinator.ReadPendingKnownSectionsAsync(CancellationToken.None).ConfigureAwait(false);
            for (var pass = 0; pass < _options.FinalizeFlushPasses && !keys.IsEmpty; pass++)
            {
                foreach (var key in keys)
                    await _coordinator.TryDeliverSectionAsync(key, _options.Delivery, _options.Pump.PageSize,
                        _options.Pump.MaxFoldPages, CancellationToken.None).ConfigureAwait(false);
                keys = await _coordinator.ReadPendingKnownSectionsAsync(CancellationToken.None).ConfigureAwait(false);
            }
            return new(keys.IsEmpty ? RuntimeSessionStopDisposition.Stopped : RuntimeSessionStopDisposition.Blocked, keys, _hub.Failure);
        }
        catch (Exception)
        {
            _hub.ReportFailure(RuntimeFailureKind.SavedState, "runtime_finalization_failed");
            return new(RuntimeSessionStopDisposition.Blocked, ImmutableArray<SectionKey>.Empty, _hub.Failure);
        }
        finally { _finalization.Release(); }
    }

    /// <summary>
    /// Serialized canonical-write retry after actor shutdown; never restarts actors or startup recovery.
    /// Cancellation detaches this caller only. The retained finalization task finishes its actual writes.
    /// </summary>
    public Task<RuntimeSessionStopResult> RetryFinalizeStopAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_stopCore is null) throw new InvalidOperationException("Stop the runtime before retrying finalization.");
            if (_lastStop is { IsFinalized: true }) return Task.FromResult(_lastStop);
            if (_retry is null || _retry.IsCompleted) _retry = RetryCoreAsync(_stopCore);
            return _retry.WaitAsync(cancellationToken);
        }
    }

    private async Task<RuntimeSessionStopResult> RetryCoreAsync(Task<RuntimeSessionStopResult> stopping)
    {
        await stopping.ConfigureAwait(false);
        var result = await FinalizeKnownAsync().ConfigureAwait(false);
        lock (_sync)
        {
            _lastStop = result;
            _state = result.IsFinalized ? RuntimeSessionState.Stopped : RuntimeSessionState.StopBlocked;
        }
        return result;
    }
}
