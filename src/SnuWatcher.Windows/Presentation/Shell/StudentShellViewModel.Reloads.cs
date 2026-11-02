using System.ComponentModel;
using SnuWatcher.Windows.Presentation.Common;

namespace SnuWatcher.Windows.Presentation.Shell;

public sealed partial class StudentShellViewModel
{
    private long dirtyGeneration, settledGeneration, forcedReloadGeneration, settledForcedGeneration;
    private bool watchReloadRunning;
    private Task watchReloadDriver = Task.CompletedTask;

    private void RequestWatchReload(bool force)
    {
        RequireUi();
        if (force) forcedReloadGeneration = unchecked(forcedReloadGeneration + 1);
        if (closed || SelectedPage != StudentPage.MyWatches || watchReloadRunning
            || !NeedsWatchReload) return;
        watchReloadRunning = true;
        // Retain ownership before releasing this prompt, bounded UI/command-admission path.
        var ready = new TaskCompletionSource();
        watchReloadDriver = ReloadWatchesAsync(ready.Task, watchReloadDriver);
        ready.SetResult();
    }

    private async Task ReloadWatchesAsync(Task ready, Task previous)
    {
        await ready.ConfigureAwait(false);
        await previous.ConfigureAwait(false);
        try
        {
            while (true)
            {
                long? target = null;
                long forcedTarget = 0;
                long readVersionBefore = 0;
                Task<UiCommandOutcome>? reading = null;
                await dispatcher.InvokeAsync(() =>
                {
                    if (closed || SelectedPage != StudentPage.MyWatches || !NeedsWatchReload)
                    { watchReloadRunning = false; return; }
                    target = dirtyGeneration; forcedTarget = forcedReloadGeneration;
                    // Successful publication and this before/admission pair share the UI lane;
                    // an older manual reload cannot slip its receipt between capture and admission.
                    readVersionBefore = Watches.SuccessfulReloadVersion;
                    reading = Watches.ReloadCommand.ExecuteAsync(routeStop.Token);
                }).ConfigureAwait(false);
                if (target is null) return;
                var result = await reading!.ConfigureAwait(false);
                if (result is UiCommandOutcome.Busy or UiCommandOutcome.Unavailable)
                {
                    await WaitForWatchReloadAvailabilityAsync(routeStop.Token).ConfigureAwait(false);
                    continue;
                }
                if (result != UiCommandOutcome.Completed || Watches.SuccessfulReloadVersion <= readVersionBefore)
                {
                    routeStop.Token.ThrowIfCancellationRequested();
                    await WatchRefreshFailedAsync().ConfigureAwait(false);
                    await dispatcher.InvokeAsync(() => watchReloadRunning = false).ConfigureAwait(false);
                    return;
                }
                await dispatcher.InvokeAsync(() =>
                {
                    settledGeneration = Math.Max(settledGeneration, target.Value);
                    settledForcedGeneration = forcedTarget;
                    if (State.IssueKey == "Ui.CommandFailed") Model.Replace(State with { IssueKey = null });
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (routeStop.IsCancellationRequested) { }
        catch (Exception)
        {
            await WatchRefreshFailedAsync().ConfigureAwait(false);
            await dispatcher.InvokeAsync(() => watchReloadRunning = false).ConfigureAwait(false);
        }
    }

    private bool NeedsWatchReload => forcedReloadGeneration != settledForcedGeneration || dirtyGeneration > settledGeneration;

    private Task WatchRefreshFailedAsync() => dispatcher.InvokeAsync(() =>
    {
        if (closed) return;
        Model.Replace(State with { IssueKey = "Ui.CommandFailed" });
        Report("ui_shell_watch_refresh_failed");
    });

    private async Task WaitForWatchReloadAvailabilityAsync(CancellationToken token)
    {
        var command = Watches.ReloadCommand;
        var available = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check() { if (command.CanExecute(null)) available.TrySetResult(); }
        void PropertyChanged(object? sender, PropertyChangedEventArgs args) => Check();
        void CanExecuteChanged(object? sender, EventArgs args) => Check();
        await dispatcher.InvokeAsync(() =>
        {
            command.PropertyChanged += PropertyChanged;
            command.CanExecuteChanged += CanExecuteChanged;
            Check(); // Subscribe first so an idle transition cannot be lost between check and join.
        }).ConfigureAwait(false);
        try { await available.Task.WaitAsync(token).ConfigureAwait(false); }
        finally
        {
            await dispatcher.InvokeAsync(() =>
            {
                command.PropertyChanged -= PropertyChanged;
                command.CanExecuteChanged -= CanExecuteChanged;
            }).ConfigureAwait(false);
        }
    }
}
