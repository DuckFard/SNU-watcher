using System.ComponentModel;
using System.Diagnostics;

namespace SnuWatcher.Windows.Presentation.Common;

public sealed partial class UiAsyncCommand
{
    private async Task<bool> PublishStateAsync()
    {
        var succeeded = true;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                succeeded = RaiseProperty(nameof(IsExecuting));
                succeeded = RaiseProperty(nameof(IsCancellationRequested)) && succeeded;
                succeeded = RaiseCanExecuteChanged() && succeeded;
            }).ConfigureAwait(false);
        }
        catch (Exception) { succeeded = false; }
        if (!succeeded) CloseAdmission();
        return succeeded;
    }

    private bool RaiseProperty(string property)
    {
        var succeeded = true;
        foreach (var observer in PropertyChanged?.GetInvocationList() ?? [])
        {
            try { ((PropertyChangedEventHandler)observer)(this, new(property)); }
            catch (Exception) { succeeded = false; }
        }
        return succeeded;
    }

    private bool RaiseCanExecuteChanged()
    {
        var succeeded = true;
        foreach (var observer in CanExecuteChanged?.GetInvocationList() ?? [])
        {
            try { ((EventHandler)observer)(this, EventArgs.Empty); }
            catch (Exception) { succeeded = false; }
        }
        return succeeded;
    }

    private void QueueNotification(bool reportFailure)
    {
        TaskCompletionSource completion;
        lock (sync)
        {
            if (disposal is not null || closed && !reportFailure) return;
            if (reportFailure) closed = true;
            pendingNotificationError |= reportFailure;
            if (notificationPosted) return;
            notificationPosted = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            notifications = notifications.IsCompleted ? completion.Task : Task.WhenAll(notifications, completion.Task);
        }
        try { dispatcher.Post(() => CompleteNotification(completion)); }
        catch (Exception)
        {
            CloseAdmission();
            lock (sync) { notificationPosted = false; pendingNotificationError = false; }
            Trace.WriteLine("ui_command_failed");
            completion.TrySetResult();
        }
    }

    private void CompleteNotification(TaskCompletionSource completion)
    {
        try
        {
            bool failed;
            lock (sync)
            {
                failed = pendingNotificationError;
                pendingNotificationError = false;
                notificationPosted = false;
            }
            if (!RaiseCanExecuteChanged()) { CloseAdmission(); failed = true; }
            if (failed) ReportErrorCallback();
        }
        finally { completion.TrySetResult(); }
    }

    private async Task ReportErrorAsync()
    {
        try { await dispatcher.InvokeAsync(ReportErrorCallback).ConfigureAwait(false); }
        catch (Exception) { Trace.WriteLine("ui_command_failed"); }
    }

    private void ReportErrorCallback()
    {
        try { reportError("ui_command_failed"); }
        catch (Exception) { Trace.WriteLine("ui_command_failed"); }
    }
}
