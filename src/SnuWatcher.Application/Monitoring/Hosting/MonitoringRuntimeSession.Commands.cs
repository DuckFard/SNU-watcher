using System.Collections.Immutable;
using SnuWatcher.Application.Migration;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Monitoring.Hosting;

public sealed partial class MonitoringRuntimeSession
{
    public Task<SubscriptionSnapshot> CreateAsync(NewSubscription command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Admit(token => MutateAsync(() => _watches.CreateAsync(command, token)), cancellationToken);
    }

    public Task<SubscriptionSnapshot> ChangeAsync(SubscriptionChange command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Admit(token => MutateAsync(() => _watches.ChangeAsync(command, token)), cancellationToken);
    }

    public Task<LegacyImportReceipt> ImportAsync(LegacyImportBatch command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Admit(token => MutateAsync(() => _watches.ImportLegacyAsync(command, token)), cancellationToken);
    }

    private async Task<T> MutateAsync<T>(Func<Task<T>> operation)
    {
        var result = await operation().ConfigureAwait(false);
        WakeActors(); // The actual commit result survives late caller/session cancellation.
        return result;
    }

    public Task<OwnedRefreshResult> RefreshAsync(SectionKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Admit(async token =>
        {
            using var waiter = CancellationTokenSource.CreateLinkedTokenSource(token, _refreshWaiters.Token);
            return await _refresh.RequestAsync(key, waiter.Token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task SetOnlineAsync(bool isOnline, CancellationToken cancellationToken = default) =>
        Admit(token => { token.ThrowIfCancellationRequested(); _monitor.SetOnline(isOnline); return Task.FromResult(true); }, cancellationToken);

    public Task SetSuspendedAsync(bool isSuspended, CancellationToken cancellationToken = default) =>
        Admit(token => { token.ThrowIfCancellationRequested(); _monitor.SetSuspended(isSuspended); return Task.FromResult(true); }, cancellationToken);

    private Task<T> Admit<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_state != RuntimeSessionState.Running || _stopping || _hub.Failure is not null)
                throw new InvalidOperationException("Runtime commands are not admitted while monitoring is stopped.");
            _commands = _commands.Where(pair => !pair.Value.IsCompleted).ToImmutableDictionary();
            if (_commands.Count >= _options.MaxAdmittedCommands)
                throw new InvalidOperationException("The runtime command queue is full. Wait for the admitted operation to finish.");
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var owned = ExecuteCommandAsync(ready.Task, operation, cancellationToken, completion);
            _commands = _commands.Add(Guid.NewGuid(), owned);
            ready.SetResult();
            return completion.Task;
        }
    }

    private async Task ExecuteCommandAsync<T>(Task ready, Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken, TaskCompletionSource<T> completion)
    {
        await ready.ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            completion.TrySetResult(await operation(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _refreshWaiters.IsCancellationRequested)
        { completion.TrySetCanceled(cancellationToken.IsCancellationRequested ? cancellationToken : _refreshWaiters.Token); }
        catch (Exception error)
        {
            var expected = error is ArgumentException or KeyNotFoundException
                || error is InvalidOperationException and not ObjectDisposedException;
            if (!expected) _hub.ReportFailure(RuntimeFailureKind.SavedState, "runtime_command_failed");
            completion.TrySetException(expected ? error : new InvalidOperationException("The runtime could not complete the saved operation."));
            // The session owns observation even if a UI waiter goes away; awaiting still throws to its caller.
            completion.Task.Exception?.Handle(static _ => true);
        }
    }
}
