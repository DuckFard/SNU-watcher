using SnuWatcher.Windows;
using Xunit;

namespace SnuWatcher.Windows.Tests;

public sealed class NotificationActivationRouterTests
{
    [Theory]
    [InlineData("action=open-registration", ActivationAction.OpenRegistration)]
    [InlineData("action=view-section", ActivationAction.ViewSection)]
    [InlineData("action=show", ActivationAction.Show)]
    public void FirstColdLifecyclePayloadDispatchesExactlyOnce(string arguments, ActivationAction action)
    {
        var delivered = new List<ActivationRequest>();

        var handled = NotificationActivationRouter.DispatchStartup(true, arguments, delivered.Add);

        Assert.True(handled);
        Assert.Equal(action, Assert.Single(delivered).Action);
    }

    [Fact]
    public void OrdinaryLaunchDoesNotInterpretLaunchTextAsANotification()
    {
        var delivered = new List<ActivationRequest>();

        var handled = NotificationActivationRouter.DispatchStartup(false, "action=open-registration", delivered.Add);

        Assert.False(handled);
        Assert.Empty(delivered);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("action=quit")]
    [InlineData("action=open-registration&url=https://evil.example/")]
    public void MalformedColdPayloadNeverReachesActionBoundary(string? arguments)
    {
        var delivered = new List<ActivationRequest>();

        Assert.Throws<ArgumentException>(() =>
            NotificationActivationRouter.DispatchStartup(true, arguments, delivered.Add));

        Assert.Empty(delivered);
    }
}
