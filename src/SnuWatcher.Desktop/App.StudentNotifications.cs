using System;
using System.Threading;
using System.Threading.Tasks;
using SnuWatcher.Application.Presentation.Preferences;

namespace SnuWatcher.Desktop;

public partial class App
{
    private readonly TaskCompletionSource<string> _studentNativeFailure =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _studentNativeDetached = 1;

    private void AttachStudentNotifications()
    {
        Dispatcher.VerifyAccess();
        if (_notifications is null || _studentSession is null) return;
        Volatile.Write(ref _studentNativeDetached, 0);
        _studentSession.Settings.PreferencesChanged += ApplyKnownStudentNotificationPreferences;
        _notifications.AvailabilityChanged += StudentNotificationAvailabilityChanged;
    }

    private void ApplyKnownStudentNotificationPreferences(UiPreferencesSnapshot snapshot)
    {
        if (Volatile.Read(ref _studentNativeDetached) != 0 || _notifications is null) return;
        try
        {
            // Only authoritative saved snapshots reach this event; editable settings never configure delivery.
            if (_notifications.ApplySavedPreferences(snapshot)) _studentHost?.NotifyDeliveryChannelChanged();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _studentNativeFailure.TrySetResult("native_saved_configuration_failed");
        }
    }

    private void StudentNotificationAvailabilityChanged()
    {
        if (Volatile.Read(ref _studentNativeDetached) != 0) return;
        try { _studentHost?.NotifyDeliveryChannelChanged(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { _studentNativeFailure.TrySetResult("native_availability_wake_failed"); }
    }

    private void DetachStudentNotifications()
    {
        if (Interlocked.Exchange(ref _studentNativeDetached, 1) != 0) return;
        if (_studentSession is not null)
            _studentSession.Settings.PreferencesChanged -= ApplyKnownStudentNotificationPreferences;
        if (_notifications is not null) _notifications.AvailabilityChanged -= StudentNotificationAvailabilityChanged;
    }
}
