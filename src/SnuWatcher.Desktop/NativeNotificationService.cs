using System;
using System.Security.Principal;
using Microsoft.Windows.AppNotifications;
using SnuWatcher.Windows;

namespace SnuWatcher.Desktop;

/// <summary>Native local notification boundary; accepted by Windows is not proof of a visible banner.</summary>
internal sealed class NativeNotificationService : IDisposable
{
    private bool _registered;
    private bool _shortcutBound;
    public event Action<ActivationRequest>? Activated;
    public event Action<string>? Diagnostic;
    public string Status { get; private set; } = "Not initialized";

    public void Initialize()
    {
        using var identity = WindowsIdentity.GetCurrent();
        NotificationElevationPolicy.EnsureUnelevated(
            new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
        NativeAppIdentity.Initialize();
        // Handler -> Register -> inspect lifecycle activation in App.OnStartup.
        AppNotificationManager.Default.NotificationInvoked += OnInvoked;
        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
            _shortcutBound = NativeShortcutBinding.BindExisting();
            Status = "Identity: " + NativeAppIdentity.ReadCurrent() +
                "; registered; Windows setting: " + AppNotificationManager.Default.Setting +
                "; installed shortcut: " + (_shortcutBound ? "identity verified" : "not found - install this candidate before testing");
        }
        catch
        {
            AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
            throw;
        }
    }

    public uint SendTest()
    {
        if (!_registered)
            throw new InvalidOperationException("Native notifications are not registered.");
        if (!_shortcutBound)
            throw new InvalidOperationException("Install and launch the candidate through its SNU Watcher shortcut before testing notifications.");
        var setting = AppNotificationManager.Default.Setting;
        if (setting != AppNotificationSetting.Enabled)
            throw new InvalidOperationException("Windows notifications are disabled: " + setting);
        // Windows opens the fixed HTTPS target directly, including when this process has exited.
        // Keep SDK display registration, but do not depend on the currently unverified COM callback for registration clicks.
        var notification = new AppNotification(RegistrationNotificationPayload.CreateTest());
        notification.Tag = "native-test";
        notification.Group = "feasibility";
        AppNotificationManager.Default.Show(notification);
        if (notification.Id == 0)
            throw new InvalidOperationException("Windows did not accept the test notification.");
        return notification.Id;
    }

    private void OnInvoked(AppNotificationManager manager, AppNotificationActivatedEventArgs arguments)
    {
        try { Activated?.Invoke(ActivationRequest.Parse(arguments.Argument)); }
        catch (ArgumentException) { Diagnostic?.Invoke("Unrecognized notification activation was ignored."); }
    }

    public void RemoveRegistration()
    {
        AppNotificationManager.Default.UnregisterAll();
        _registered = false;
        Status = "Notification registration removed";
    }

    public void Dispose()
    {
        AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
        if (_registered)
        {
            AppNotificationManager.Default.Unregister();
            _registered = false;
        }
    }
}
