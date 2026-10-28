namespace SnuWatcher.Windows;

/// <summary>Routes the SDK's first cold-start payload separately from later notification callbacks.</summary>
public static class NotificationActivationRouter
{
    /// <summary>
    /// Dispatches a lifecycle notification once after validating its closed action set.
    /// Ordinary launch arguments are never interpreted as notification actions.
    /// </summary>
    public static bool DispatchStartup(bool isAppNotification, string? arguments, Action<ActivationRequest> dispatch)
    {
        if (!isAppNotification) return false;
        dispatch(ActivationRequest.Parse(arguments));
        return true;
    }
}
