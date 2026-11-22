using SnuWatcher.Application.Sources;

namespace SnuWatcher.Infrastructure.Snu.Gateway;

public sealed partial class SnuCourseGateway
{
    private int _callers;
    private bool _closed;
    private TaskCompletionSource _callersDrained = CompletedSignal();
    private Task? _shutdown;

    private async Task<SourceRead<T>> AdmitAsync<T>(CancellationToken token, Func<Task<SourceRead<T>>> operation) where T : class
    {
        token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_callers >= _options.MaxCallers) return Busy<T>();
            if (_callers++ == 0) _callersDrained = NewSignal();
        }
        try { return await operation().ConfigureAwait(false); }
        finally
        {
            TaskCompletionSource? drained;
            lock (_sync) { drained = --_callers == 0 ? _callersDrained : null; }
            drained?.TrySetResult();
        }
    }

    private void Detach(Flight flight)
    {
        bool cancel;
        bool cleanup;
        lock (_sync)
        {
            flight.Waiters--;
            cancel = flight.Waiters == 0 && !flight.ActualComplete && RequestStop(flight);
            cleanup = ReserveCleanup(flight);
        }
        if (cancel) CancelFlight(flight);
        if (cleanup) DisposeFlight(flight);
    }

    // Called under _sync. Nonjoinable is visible before cancellation callbacks execute outside the lock.
    private static bool RequestStop(Flight flight)
    {
        if (flight.StopRequested) return false;
        flight.StopRequested = true;
        flight.CancelRunning = true;
        return true;
    }

    private void CancelFlight(Flight flight)
    {
        try { flight.Stop.Cancel(); }
        catch (AggregateException) { /* A dependency callback cannot mask a detached caller's result. */ }
        finally
        {
            bool cleanup;
            lock (_sync) { flight.CancelRunning = false; cleanup = ReserveCleanup(flight); }
            if (cleanup) DisposeFlight(flight);
        }
    }

    private static bool ReserveCleanup(Flight flight)
    {
        if (!flight.ActualComplete || flight.Waiters != 0 || flight.CancelRunning || flight.Disposing) return false;
        flight.Disposing = true;
        return true;
    }

    private void DisposeFlight(Flight flight)
    {
        flight.Stop.Dispose();
        lock (_sync) _allFlights.Remove(flight);
        flight.Drained.TrySetResult();
    }

    /// <summary>
    /// Stops admission and awaits actual source completion and caller detachment. It cannot abandon an
    /// uncooperative HTTP operation. The supplied source remains host-owned and must be disposed later.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_shutdown is not null) return new(_shutdown);
            _closed = true;
            var cancel = _allFlights.Where(flight => !flight.ActualComplete && RequestStop(flight)).ToArray();
            var drains = _allFlights.Select(flight => flight.Drained.Task).Append(_callersDrained.Task).ToArray();
            // Cancel callbacks may reenter the gateway. They run after this lock is released.
            _shutdown = Task.Run(() => ShutdownAsync(cancel, drains));
            return new(_shutdown);
        }
    }

    private async Task ShutdownAsync(IEnumerable<Flight> cancel, IEnumerable<Task> drains)
    {
        foreach (var flight in cancel) CancelFlight(flight);
        await Task.WhenAll(drains).ConfigureAwait(false);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource CompletedSignal()
    {
        var signal = NewSignal();
        signal.SetResult();
        return signal;
    }
}
