namespace SnuWatcher.Windows;

/// <summary>Rejects unsupported elevated notification senders before native registration begins.</summary>
public static class NotificationElevationPolicy
{
    /// <summary>Fixed relaunch guidance that is safe to include in user-facing startup diagnostics.</summary>
    public const string Guidance =
        "Windows notifications are unavailable when SNU Watcher runs as administrator. Close the app and launch the installed shortcut normally.";

    /// <summary>Allows an ordinary launch or reports the specific unsupported elevation condition.</summary>
    public static void EnsureUnelevated(bool isElevated)
    {
        if (isElevated) throw new UnsupportedNotificationElevationException();
    }
}

/// <summary>A known startup failure carrying only fixed guidance, without paths or arbitrary exception text.</summary>
public sealed class UnsupportedNotificationElevationException : InvalidOperationException
{
    /// <summary>Creates the unsupported-elevation failure with its fixed relaunch guidance.</summary>
    public UnsupportedNotificationElevationException() : base(NotificationElevationPolicy.Guidance) { }
}
