namespace SnuWatcher.Windows.Presentation.Common;

public sealed partial class UiAsyncCommand
{
    private async Task<UiCommandOutcome> RunAsync(Task ready, Task<UiCommandOutcome> previous, Execution state)
    {
        await ready.ConfigureAwait(false);
        var outcome = UiCommandOutcome.Failed;
        var failed = false;
        CancellationTokenRegistration registration = default;
        try
        {
            // A reentrant execution cannot discard the previous final publication's ownership.
            await previous.ConfigureAwait(false);
            registration = state.Caller.Register(() => RequestCancellation(state));
            if (!await PublishStateAsync().ConfigureAwait(false)) failed = true;
            else
            {
                state.Cancellation.Token.ThrowIfCancellationRequested();
                var priorOwner = InsideOperation.Value;
                InsideOperation.Value = this;
                try { await execute(state.Cancellation.Token).ConfigureAwait(false); }
                finally { InsideOperation.Value = priorOwner; }
                outcome = UiCommandOutcome.Completed;
            }
        }
        catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
        { outcome = UiCommandOutcome.Canceled; }
        catch (Exception) { failed = true; }
        try { await registration.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { failed = true; }
        if (!await FinishCancellationAsync(state).ConfigureAwait(false)) failed = true;
        state.Cancellation.Dispose();
        lock (sync) state.Running = false;
        if (!await PublishStateAsync().ConfigureAwait(false)) failed = true;
        if (!failed) return outcome;
        await ReportErrorAsync().ConfigureAwait(false);
        return UiCommandOutcome.Failed;
    }

    private void RequestCancellation(Execution state)
    {
        TaskCompletionSource notificationsReady;
        TaskCompletionSource<bool> publicationReady;
        lock (sync)
        {
            if (!state.Running || !state.AcceptCancellation || state.CancellationRequested) return;
            state.CancellationRequested = true;
            notificationsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            publicationReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            state.CancellationNotifications = notificationsReady.Task;
            state.CancellationPublication = publicationReady.Task;
        }
        // Register both ownership receipts before invoking either callback path.
        _ = CancelCallbacksAsync(state, notificationsReady);
        _ = PublishCancellationAsync(publicationReady);
    }

    private static async Task CancelCallbacksAsync(Execution state, TaskCompletionSource completion)
    {
        try { await state.Cancellation.CancelAsync().ConfigureAwait(false); completion.TrySetResult(); }
        catch (Exception error) { completion.TrySetException(error); }
    }

    private async Task PublishCancellationAsync(TaskCompletionSource<bool> completion)
    {
        completion.TrySetResult(await PublishStateAsync().ConfigureAwait(false));
    }

    private async Task<bool> FinishCancellationAsync(Execution state)
    {
        Task callbacks;
        Task<bool> publication;
        lock (sync)
        {
            state.AcceptCancellation = false;
            callbacks = state.CancellationNotifications;
            publication = state.CancellationPublication;
        }
        var succeeded = true;
        try { await callbacks.ConfigureAwait(false); }
        catch (Exception) { succeeded = false; }
        return await publication.ConfigureAwait(false) && succeeded;
    }

    private async Task DisposeCoreAsync(Task ready, Task<UiCommandOutcome> running, Execution? state)
    {
        await ready.ConfigureAwait(false);
        if (state is not null) RequestCancellation(state);
        await running.ConfigureAwait(false);
        Task pending;
        lock (sync) pending = notifications;
        await pending.ConfigureAwait(false);
        if (!await PublishStateAsync().ConfigureAwait(false))
            await ReportErrorAsync().ConfigureAwait(false);
    }
}
