namespace SnuWatcher.Windows;

/// <summary>Requires bounded notification removal before persistent registration cleanup.</summary>
public static class NotificationCleanupPolicy
{
    /// <summary>The longest removal wait allowed by the uninstall preflight.</summary>
    public static TimeSpan MaximumRemovalWait { get; } = TimeSpan.FromSeconds(10);

    /// <summary>Clears retained notifications, then unregisters only after removal succeeds.</summary>
    /// <param name="isRegistered">Whether native registration completed in this process.</param>
    /// <param name="clearNotifications">Starts removal and returns its actual completion task.</param>
    /// <param name="unregister">Removes persistent registrations after successful removal.</param>
    /// <param name="removalTimeout">A nonnegative wait no longer than ten seconds.</param>
    /// <exception cref="ArgumentNullException">An operation is missing.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The wait is outside its allowed bounds.</exception>
    /// <exception cref="InvalidOperationException">Native registration did not complete.</exception>
    /// <exception cref="TimeoutException">Removal exceeded the deadline; unregister is not attempted.</exception>
    /// <remarks>Operation failures propagate. A timeout does not claim that the native operation was canceled.</remarks>
    public static async Task ClearAndUnregisterAsync(bool isRegistered,
        Func<Task> clearNotifications, Action unregister, TimeSpan removalTimeout)
    {
        ArgumentNullException.ThrowIfNull(clearNotifications);
        ArgumentNullException.ThrowIfNull(unregister);
        if (removalTimeout < TimeSpan.Zero || removalTimeout > MaximumRemovalWait)
            throw new ArgumentOutOfRangeException(nameof(removalTimeout));
        if (!isRegistered)
            throw new InvalidOperationException("Native notifications are not registered.");

        await clearNotifications().WaitAsync(removalTimeout);
        unregister();
    }
}
