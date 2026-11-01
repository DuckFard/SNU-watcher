using SnuWatcher.Application.Monitoring.Hosting;

namespace SnuWatcher.Windows.Hosting;

public sealed partial class WindowsMonitoringHost
{
    private Task<WindowsHostStopResult> BeginStop(bool retry, CancellationToken waiter)
    {
        waiter.ThrowIfCancellationRequested();
        TaskCompletionSource<WindowsHostStopResult>? promise = null;
        Task<WindowsHostStopResult> result;
        lock (_sync)
        {
            if (State == WindowsMonitoringHostState.Stopped)
                return Task.FromResult(new WindowsHostStopResult(State, _logicalStop, null));
            if (retry)
            {
                if (_retryTask is { IsCompleted: false }) return _retryTask.WaitAsync(waiter);
                if (State == WindowsMonitoringHostState.Stopping && _stopTask is not null) return _stopTask.WaitAsync(waiter);
                if (State != WindowsMonitoringHostState.StopBlocked) throw new InvalidOperationException("Only blocked finalization can be retried.");
            }
            else if (_stopTask is not null) return _stopTask;
            _closing = true;
            _controls.Writer.TryComplete();
            _stopping.TrySetResult();
            Volatile.Write(ref _state, (int)WindowsMonitoringHostState.Stopping);
            promise = new(TaskCreationOptions.RunContinuationsAsynchronously);
            result = promise.Task;
            if (retry) _retryTask = result; else _stopTask = result;
        }
        // Publish the one task before invoking cancellation callbacks, which may reenter this host.
        _stopWorker = CompleteStopAsync(promise, retry);
        return retry ? result.WaitAsync(waiter) : result;
    }

    private async Task CompleteStopAsync(TaskCompletionSource<WindowsHostStopResult> promise, bool retry)
    {
        WindowsHostStopResult result;
        try { result = await StopCoreAsync(retry).ConfigureAwait(false); }
        catch (Exception) { result = Blocked("runtime_stop_failed"); }
        lock (_sync)
        {
            Volatile.Write(ref _state, (int)result.State);
            if (retry) _stopTask = Task.FromResult(result);
        }
        promise.TrySetResult(result);
    }

    private async Task<WindowsHostStopResult> StopCoreAsync(bool retry)
    {
        // Session stop requests monitor, pump and refresh cancellation immediately, before waiting
        // for the actual channel. Do not postpone that request behind external controls or source IO.
        var logicalTask = _logicalStop?.IsFinalized == true ? Task.FromResult(_logicalStop!)
            : retry ? _session.RetryFinalizeStopAsync(CancellationToken.None) : _session.StopAsync();
        _logicalStop = await logicalTask.ConfigureAwait(false);
        await _controlsWorker.ConfigureAwait(false);
        if (!_gatewayClosed)
        { await _source.CloseGateway().ConfigureAwait(false); _gatewayClosed = true; }
        if (!_sourceClosed)
        {
            if (_source.Diagnostic() == "source_policy_storage_failed")
            {
                try { await _source.Recover(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { return Blocked("source_policy_flush_failed"); }
                if (_source.Diagnostic() == "source_policy_storage_failed") return Blocked("source_policy_flush_failed");
            }
            if (!_logicalStop.IsFinalized) return Blocked("delivery_results_pending");
            await _source.CloseSource().ConfigureAwait(false);
            _sourceClosed = true;
        }
        // A blocked session has already quiesced its logical workers. Keep its coordinator, store,
        // gate and owner so canonical known results can be finalized without restarting any actor.
        if (!_logicalStop.IsFinalized) return Blocked("delivery_results_pending");
        await _session.DisposeAsync().ConfigureAwait(false);
        if (!_storeClosed) { await _store.DisposeAsync().ConfigureAwait(false); _storeClosed = true; }
        if (!_gateClosed) { _gate.Dispose(); _gateClosed = true; }
        if (!_ownerClosed) { await _owner.DisposeAsync().ConfigureAwait(false); _ownerClosed = true; }
        return new(WindowsMonitoringHostState.Stopped, _logicalStop, null);
    }

    private WindowsHostStopResult Blocked(string code) => new(WindowsMonitoringHostState.StopBlocked, _logicalStop, code);
}
