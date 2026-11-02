using SnuWatcher.Application.Delivery.Batches;

namespace SnuWatcher.Windows.Notifications;

public sealed partial class NativeNotificationChannel
{
    private Task<T> Enqueue<T>(Func<T> operation, Func<T>? whenFull = null)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            if (closing)
                return whenFull is null ? Task.FromException<T>(new ObjectDisposedException(nameof(NativeNotificationChannel)))
                    : Task.FromResult(whenFull()); // A refused send proves no native admission, even after Ready raced stop.
            if (pending.Count >= maxPending)
                return whenFull is null ? Task.FromException<T>(new InvalidOperationException("The native operation queue is full."))
                    : Task.FromResult(whenFull());
            pending.Enqueue(() =>
            {
                try { completion.TrySetResult(operation()); }
                catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
                catch (Exception error) { completion.TrySetException(error); }
            });
            signal.Set();
        }
        return completion.Task;
    }

    private void WorkerMain()
    {
        try
        {
            while (true)
            {
                Action? operation; bool changed;
                lock (sync)
                {
                    if (probeRequested && !closing) { probeRequested = false; operation = () => ProbeKnown(false); }
                    else if (pending.TryDequeue(out var next)) operation = next;
                    else if (closing) break;
                    else operation = null;
                    var before = EffectiveReadiness(); busy = operation is not null;
                    changed = before != EffectiveReadiness();
                }
                if (operation is null) { signal.WaitOne(); continue; }
                if (changed) NotifyAvailability();
                try { operation(); }
                catch { SetReadiness(BatchChannelReadiness.Unavailable); Report("native_worker_failed"); }
                finally
                {
                    lock (sync) { var before = EffectiveReadiness(); busy = false; changed = before != EffectiveReadiness(); }
                    if (changed) NotifyAvailability();
                }
            }
        }
        finally { signal.Dispose(); stopped.TrySetResult(); }
    }

    // Called only with sync held. Busy is deliberately not Ready; pre-seal probes never queue behind Show.
    private BatchChannelReadiness EffectiveReadiness() => closing ? BatchChannelReadiness.Unavailable
        : configuration is null || busy ? BatchChannelReadiness.Initializing : readiness;

    private BatchChannelReadiness ProbeKnown(bool legacy)
    {
        lock (sync)
        {
            if (closing) return BatchChannelReadiness.Unavailable;
            if (!legacy && configuration is null) return BatchChannelReadiness.Initializing;
        }
        BatchChannelReadiness value;
        try { value = driver.Probe(); if (!Enum.IsDefined(value)) value = BatchChannelReadiness.Unavailable; }
        catch { value = BatchChannelReadiness.Unavailable; Report("native_probe_failed"); }
        SetReadiness(value);
        return value;
    }

    private void SetReadiness(BatchChannelReadiness value)
    {
        bool changed;
        lock (sync) { var before = EffectiveReadiness(); readiness = value; changed = before != EffectiveReadiness(); }
        if (changed) NotifyAvailability();
    }

    private void NotifyAvailability()
    {
        if (AvailabilityChanged is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch { Report("native_availability_observer_failed"); }
    }

    private void Report(string code) { try { diagnostic?.Invoke(code); } catch { /* Diagnostic observers cannot change OS evidence. */ } }
}
