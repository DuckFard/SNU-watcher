using SnuWatcher.Application.Presentation.Preferences;

namespace SnuWatcher.Application.Monitoring.Hosting;

public sealed partial class MonitoringRuntimeSession
{
    // Keep the actual operation in the one bounded command registry. A UI waiter may leave promptly;
    // its linked token cancels only this gateway/store waiter, never the session or another query.
    // A dependency that returns cancellation before physical work ends remains the external host's
    // responsibility: the host drains its gateway before disposing source/store/owner resources.
    private Task<T> AdmitUiOperation<T>(Func<CancellationToken, Task<T>> operation, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        var retained = Admit(async token =>
        {
            using var owned = CancellationTokenSource.CreateLinkedTokenSource(token, _refreshWaiters.Token);
            owned.Token.ThrowIfCancellationRequested();
            return await operation(owned.Token).ConfigureAwait(false);
        }, caller);
        return retained.WaitAsync(caller);
    }

    private Task<T> AdmitPresentation<T>(Func<RuntimePresentationServices, CancellationToken, Task<T>> operation,
        CancellationToken caller, bool allowNull = false)
    {
        caller.ThrowIfCancellationRequested();
        var services = _presentationServices
            ?? throw new InvalidOperationException("Presentation services are unavailable for this runtime.");
        return AdmitUiOperation(token => RunPresentationAsync(() => operation(services, token), token, allowNull), caller);
    }

    private async Task<T> RunPresentationAsync<T>(Func<Task<T>> operation, CancellationToken token, bool allowNull = false)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var result = await operation().ConfigureAwait(false);
            if (result is null && !allowNull) throw new InvalidOperationException();
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (UiPreferencesReplayConflictException) { throw; }
        catch (Exception)
        {
            _hub.ReportFailure(RuntimeFailureKind.SavedState, "runtime_presentation_failed");
            throw new InvalidOperationException("The runtime could not access saved presentation data.");
        }
    }
}
