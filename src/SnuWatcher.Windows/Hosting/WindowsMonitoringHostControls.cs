using SnuWatcher.Application.Monitoring.SourcePolicy;

namespace SnuWatcher.Windows.Hosting;

public sealed partial class WindowsMonitoringHost
{
    /// <summary>Wakes pending delivery after a known native availability change without source work.</summary>
    public bool NotifyDeliveryChannelChanged()
    {
        lock (_sync)
        {
            if (_closing) return false;
            return _session.NotifyDeliveryChannelChanged();
        }
    }

    private sealed record SourceControl(bool Recover, long Revision, TaskCompletionSource<SourceAccessResumeResult?> Promise);

    private Task<SourceAccessResumeResult?> QueueControlAsync(bool recover, long revision, CancellationToken waiter)
    {
        waiter.ThrowIfCancellationRequested();
        var promise = new TaskCompletionSource<SourceAccessResumeResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closing || State != WindowsMonitoringHostState.Running, this);
            if (_controlCount >= _maxControls) throw new InvalidOperationException("The source control queue is full.");
            _controlCount++;
            if (!_controls.Writer.TryWrite(new(recover, revision, promise)))
            { _controlCount--; throw new InvalidOperationException("Source control admission is closed."); }
        }
        // Only the UI wait is cancelled. One host-owned worker owns the actual queued/running control.
        return promise.Task.WaitAsync(waiter);
    }

    private async Task RunControlsAsync()
    {
        await foreach (var control in _controls.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                bool closing;
                lock (_sync) closing = _closing;
                if (closing) throw new ObjectDisposedException(nameof(WindowsMonitoringHost));
                SourceAccessResumeResult? result = null;
                if (control.Recover) await _source.Recover(CancellationToken.None).ConfigureAwait(false);
                else result = await _source.Resume(control.Revision, CancellationToken.None).ConfigureAwait(false);
                _session.Wake();
                control.Promise.TrySetResult(result);
            }
            catch (Exception error)
            {
                control.Promise.TrySetException(error);
                // A cancelled UI waiter may never await this promise; the owner observes its fault.
                _ = control.Promise.Task.Exception;
                try { if (_source.Diagnostic() == "source_policy_storage_failed") _sourceFault.TrySetResult(); }
                catch (Exception) { _sourceFault.TrySetResult(); }
            }
            finally { lock (_sync) _controlCount--; }
        }
    }

    private async Task ObserveFailuresAsync()
    {
        try
        {
            var completed = await Task.WhenAny(_session.FailureTask, _sourceFault.Task, _stopping.Task).ConfigureAwait(false);
            bool closing;
            lock (_sync) closing = _closing;
            if (!closing && completed != _stopping.Task) await StopAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Stop remains explicit/retryable and ownership is retained; no observer can declare closure.
            Volatile.Write(ref _state, (int)WindowsMonitoringHostState.StopBlocked);
        }
    }
}
