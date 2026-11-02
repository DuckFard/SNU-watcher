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
            disposal = StartDisposal(ready.Task, hintDriver, onboardingDriver);
            ready.SetResult();
            return new(disposal);
        }
    }

    private Task StartDisposal(Task ready, Task hints, Task onboarding)
    {
        // WPF publication can reenter disposal while carrying a command's ownership context.
        // The retained driver must drain that command from outside its own-operation context.
        if (ExecutionContext.IsFlowSuppressed()) return Task.Run(() => DisposeCoreAsync(ready, hints, onboarding));
        using (ExecutionContext.SuppressFlow()) return Task.Run(() => DisposeCoreAsync(ready, hints, onboarding));
    }

    private async Task DisposeCoreAsync(Task ready, Task hints, Task onboarding)
    {
        await ready.ConfigureAwait(false);
        var cancellation = hintStop.CancelAsync();
        var nativeCommandDrain = Task.WhenAll(NativeTestCommand.DisposeAsync().AsTask(), RefreshNativeStatusCommand.DisposeAsync().AsTask());
        var nativeLaneDrain = DisposeNativeLaneAsync(nativeCommandDrain);
        var diagnosticsDrain = DisposeDiagnosticsLaneAsync(Task.WhenAll(RefreshDiagnosticsCommand.DisposeAsync().AsTask(),
            ExportDiagnosticsCommand.DisposeAsync().AsTask()));
        var commandDrain = Task.WhenAll(commands.Select(command => command.DisposeAsync().AsTask()));
        try { await Task.WhenAll(cancellation, commandDrain, nativeLaneDrain, diagnosticsDrain, hints, onboarding).ConfigureAwait(false); }
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
