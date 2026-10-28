using System;
using System.Security.Principal;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
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
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Notifications are unavailable when elevated. Launch the installed app normally.");
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
        var notification = new AppNotificationBuilder()
            .AddArgument("action", "open-registration")
            .AddText("SNU Watcher - notification test")
            .AddText("This is a test, not a seat opening. Click to open official SNU registration.")
            .AddButton(new AppNotificationButton("Open registration").AddArgument("action", "open-registration"))
            .AddButton(new AppNotificationButton("View app").AddArgument("action", "view-section"))
            .BuildNotification();
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
