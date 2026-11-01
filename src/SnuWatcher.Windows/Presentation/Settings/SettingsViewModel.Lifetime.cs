namespace SnuWatcher.Windows.Presentation.Settings;

public sealed partial class SettingsViewModel
{
    /// <summary>Closes admission, cancels waiter ownership and drains actual command/hint work before releasing resources.</summary>
    public ValueTask DisposeAsync()
    {
        lock (lifetimeSync)
        {
            if (disposal is not null) return new(disposal);
            closed = true;
            pendingHint?.Completion.TrySetCanceled();
            claimedHint?.Completion.TrySetCanceled();
            onboardingIntent?.Completion.TrySetCanceled();
            pendingHint = null;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = DisposeCoreAsync(ready.Task, hintDriver, onboardingDriver);
            ready.SetResult();
            return new(disposal);
        }
    }

    private async Task DisposeCoreAsync(Task ready, Task hints, Task onboarding)
    {
        await ready.ConfigureAwait(false);
        var cancellation = hintStop.CancelAsync();
        var commandDrain = Task.WhenAll(commands.Select(command => command.DisposeAsync().AsTask()));
        try { await Task.WhenAll(cancellation, commandDrain, hints, onboarding).ConfigureAwait(false); }
        finally
        {
            Model.PropertyChanged -= ModelChanged;
            PropertyChanged = null;
            PreferencesChanged = null;
            operationLane.Dispose();
            hintStop.Dispose();
        }
    }
}
