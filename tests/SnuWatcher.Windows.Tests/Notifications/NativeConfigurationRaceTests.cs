using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Windows.Notifications;
using Xunit;

namespace SnuWatcher.Windows.Tests.Notifications;

public sealed class NativeConfigurationRaceTests
{
    [Fact]
    public async Task NewKnownPreferencesDuringPreparationRefuseOldPayloadAndNextOperationUsesNewSnapshot()
    {
        var driver = new NativeTestDriver();
        await using var channel = new NativeNotificationChannel(driver, new NativeTestClock());
        channel.ApplySavedPreferences(NativeNotificationFixture.Preferences()); await channel.ReadStatusAsync();
        var shows = 0;
        driver.PrepareCallback = _ =>
        {
            channel.ApplySavedPreferences(NativeNotificationFixture.Preferences(2, UiLanguage.Korean, true));
            return new NativePrepared(() => { shows++; return new NativeShowResult(42); });
        };
        var old = await channel.SendAsync(NativeNotificationFixture.Submission(), default);
        Assert.Equal(BatchNoSubmissionReason.ChannelNotReady, old.NoSubmissionReason);
        Assert.Equal(0, shows);
        driver.PrepareCallback = _ => new NativePrepared(() => { shows++; return new NativeShowResult(43); });
        var next = await channel.SendTestAsync();
        Assert.Equal(BatchChannelOutcome.OsAccepted, next.Result.Outcome); Assert.Equal(1, shows);
        Assert.Contains("알림 테스트", driver.Payloads.Last().Xml);
        Assert.Contains("silent=\"true\"", driver.Payloads.Last().Xml);
    }

    [Fact]
    public async Task PreferenceChangeAfterEnteredShowCannotDiscardActualAcceptance()
    {
        var driver = new NativeTestDriver();
        await using var channel = new NativeNotificationChannel(driver, new NativeTestClock());
        channel.ApplySavedPreferences(NativeNotificationFixture.Preferences()); await channel.ReadStatusAsync();
        driver.PrepareCallback = _ => new NativePrepared(() =>
        {
            channel.ApplySavedPreferences(NativeNotificationFixture.Preferences(2, UiLanguage.Korean, true));
            return new NativeShowResult(42);
        });
        Assert.Equal(BatchChannelOutcome.OsAccepted, (await channel.SendAsync(NativeNotificationFixture.Submission(), default)).Outcome);
    }
}
