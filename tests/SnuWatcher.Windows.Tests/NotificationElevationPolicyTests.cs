using SnuWatcher.Windows;
using Xunit;

namespace SnuWatcher.Windows.Tests;

public sealed class NotificationElevationPolicyTests
{
    [Fact]
    public void OrdinaryLaunchCanContinueToNotificationInitialization()
    {
        var error = Record.Exception(() => NotificationElevationPolicy.EnsureUnelevated(false));

        Assert.Null(error);
    }

    [Fact]
    public void ElevatedLaunchReportsKnownGuidanceToRelaunchNormally()
    {
        var error = Assert.Throws<UnsupportedNotificationElevationException>(() =>
            NotificationElevationPolicy.EnsureUnelevated(true));

        Assert.Equal(
            "Windows notifications are unavailable when SNU Watcher runs as administrator. Close the app and launch the installed shortcut normally.",
            error.Message);
    }
}
