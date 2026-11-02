using SnuWatcher.Application.Persistence;

namespace SnuWatcher.Windows.Presentation.Shell;

public sealed partial class StudentShellViewModel
{
    private readonly object lifetime = new();
    private readonly CancellationTokenSource routeStop = new();
    private Task routeDriver = Task.CompletedTask;
    private Task? disposal;
    private Guid? pendingCreatedWatch;
    private bool routeRunning;

    private void WatchCreated(SubscriptionSnapshot receipt)
    {
        RequireUi();
        if (closed) return;
        pendingCreatedWatch = receipt.Id;
        Model.Replace(State with { SelectedPage = StudentPage.MyWatches });
        if (routeRunning) return;
        routeRunning = true;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        routeDriver = RouteCreatedWatchAsync(ready.Task, routeDriver);
        ready.SetResult();
    }

    private async Task RouteCreatedWatchAsync(Task ready, Task previous)
    {
        await ready.ConfigureAwait(false);
        await previous.ConfigureAwait(false);
        try
        {
            while (true)
            {
                Guid? id = null;
                await dispatcher.InvokeAsync(() =>
                {
                    if (closed || pendingCreatedWatch is null) { routeRunning = false; return; }
                    id = pendingCreatedWatch; pendingCreatedWatch = null;
                }).ConfigureAwait(false);
                if (id is null) return;
                try
                {
                    Task reload = Task.CompletedTask;
                    await dispatcher.InvokeAsync(() =>
                    {
                        if (Watches.State.IsLoaded) { RequestWatchReload(force: true); reload = watchReloadDriver; }
                    }).ConfigureAwait(false);
                    await reload.ConfigureAwait(false);
                    routeStop.Token.ThrowIfCancellationRequested();
                    await Watches.SelectWatchAsync(id.Value, routeStop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (routeStop.IsCancellationRequested) { throw; }
                catch (Exception) { await SavedStateFailedAsync().ConfigureAwait(false); }
            }
        }
        catch (OperationCanceledException) when (routeStop.IsCancellationRequested) { }
        catch (Exception) { await SavedStateFailedAsync().ConfigureAwait(false); }
    }

    /// <summary>Closes shell admission, detaches listeners and drains shell-owned work. Children remain owned by the desktop.</summary>
    public ValueTask DisposeAsync()
    {
        lock (lifetime) return new(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await dispatcher.InvokeAsync(() =>
        {
            closed = true; pendingCreatedWatch = null; pendingTermHint = null;
            Settings.PreferencesChanged -= PreferencesAccepted;
            Search.WatchSelection.WatchCreated -= WatchCreated;
            Search.Model.PropertyChanged -= SearchSelectionChanged;
            Model.PropertyChanged -= ModelChanged;
        }).ConfigureAwait(false);
        var cancellation = routeStop.CancelAsync();
        var drains = commands.Select(command => command.DisposeAsync().AsTask()).ToArray();
        await Task.WhenAll(drains.Append(cancellation).Append(routeDriver).Append(watchReloadDriver).Append(termHintDriver)).ConfigureAwait(false);
        routeStop.Dispose();
    }
}
