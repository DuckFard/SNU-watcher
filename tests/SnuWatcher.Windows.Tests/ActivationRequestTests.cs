using SnuWatcher.Windows;
using Xunit;

namespace SnuWatcher.Windows.Tests;

public sealed class ActivationRequestTests
{
    [Theory]
    [InlineData("action=open-registration", ActivationAction.OpenRegistration)]
    [InlineData("action=view-section", ActivationAction.ViewSection)]
    [InlineData("action=show", ActivationAction.Show)]
    public void AllowsOnlyRecognizedActions(string input, ActivationAction expected)
    {
        Assert.Equal(expected, ActivationRequest.Parse(input).Action);
    }

    [Theory]
    [InlineData("action=open-registration&url=https://evil.example/")]
    [InlineData("action=show&action=open-registration")]
    [InlineData("action=run-command")]
    [InlineData("action=%6Fpen-registration")]
    [InlineData("ACTION=show")]
    [InlineData("action=show\n")]
    [InlineData("action=quit")]
    public void RejectsUntrustedOrAmbiguousNotificationArguments(string input)
    {
        Assert.Throws<ArgumentException>(() => ActivationRequest.Parse(input));
    }

    [Fact]
    public void RejectsOversizedPayloadBeforeParsing()
    {
        Assert.Throws<ArgumentException>(() => ActivationRequest.Parse(new string('a', 513)));
    }

    [Fact]
    public void RegistrationTargetIsFixedAndHasNoArbitraryUriInput()
    {
        Assert.Equal("https://sugang.snu.ac.kr/sugang/co/co010.action", OfficialDestinations.Registration.AbsoluteUri);
        Assert.Equal("https://snugenie.snu.ac.kr/", OfficialDestinations.Genie.AbsoluteUri);
    }

    [Fact]
    public void InstallerQuitIsAcceptedOnlyThroughSameUserLocalTransport()
    {
        Assert.Equal(ActivationAction.Quit, ActivationRequest.ParseLocal("action=quit").Action);
        Assert.Throws<ArgumentException>(() => ActivationRequest.Parse("action=quit"));
    }

    [Theory]
    [InlineData(ActivationAction.Show)]
    [InlineData(ActivationAction.OpenRegistration)]
    [InlineData(ActivationAction.ViewSection)]
    [InlineData(ActivationAction.Quit)]
    public void LocalActionsRoundTripWithoutAUri(ActivationAction action)
    {
        Assert.Equal(action, ActivationRequest.ParseLocal(new ActivationRequest(action).Encode()).Action);
    }

    [Fact]
    public void InvalidEnumActionIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActivationRequest((ActivationAction)99));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("--unregister-notifications", false)]
    [InlineData("----AppNotificationActivated:", true)]
    [InlineData("----AppNotificationActivated:ignored", true)]
    public void IdentifiesColdNotificationLaunchMarker(string argument, bool expected)
    {
        Assert.Equal(expected, ActivationRequest.IsNotificationLaunch([argument]));
    }
}
