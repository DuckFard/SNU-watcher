using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Windows.Notifications;
using Xunit;

namespace SnuWatcher.Windows.Tests.Notifications;

public sealed class NativeChannelTests
{
    [Fact]
    public async Task UnconfiguredAndDisabledAreFastAndNeverSubmit()
    {
        var driver = new NativeTestDriver();
        await using var channel = new NativeNotificationChannel(driver, new NativeTestClock());
        Assert.Equal(BatchChannelReadiness.Initializing, await channel.ReadReadinessAsync(default));
        Assert.Equal(BatchNoSubmissionReason.ChannelNotReady, (await channel.SendAsync(NativeNotificationFixture.Submission(), default)).NoSubmissionReason);
        driver.Readiness = BatchChannelReadiness.Disabled;
        Assert.True(channel.ApplySavedPreferences(NativeNotificationFixture.Preferences()));
        var status = await channel.ReadStatusAsync();
        Assert.Equal(BatchChannelReadiness.Disabled, status.Readiness); Assert.False(status.CanSendTest);
        Assert.Equal(BatchNoSubmissionReason.ChannelNotReady, (await channel.SendAsync(NativeNotificationFixture.Submission(), default)).NoSubmissionReason);
        Assert.Equal(0, driver.Shows);
    }

    [Fact]
    public async Task KnownRevisionAppliesLanguageAndSoundTogetherAndNeverRegresses()
    {
        var driver = new NativeTestDriver();
        await using var channel = new NativeNotificationChannel(driver, new NativeTestClock());
        var en = NativeNotificationFixture.Preferences();
        Assert.True(channel.ApplySavedPreferences(en)); Assert.False(channel.ApplySavedPreferences(en));
        Assert.True(channel.ApplySavedPreferences(NativeNotificationFixture.Preferences(2, UiLanguage.Korean, true)));
        Assert.False(channel.ApplySavedPreferences(en));
        Assert.Throws<ArgumentException>(() => channel.ApplySavedPreferences(NativeNotificationFixture.Preferences(2)));
        var receipt = await channel.SendTestAsync();
        Assert.Equal(BatchChannelOutcome.OsAccepted, receipt.Result.Outcome);
        var payload = Assert.Single(driver.Payloads);
        Assert.Contains("알림 테스트", payload.Xml); Assert.Contains("silent=\"true\"", payload.Xml);
    }

    [Theory]
    [InlineData(121, false)]
    [InlineData(20, true)]
    [InlineData(-1, false)]
    public async Task QueuedWindowIsRecheckedImmediatelyBeforeShow(int elapsedSeconds, bool expires)
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var driver = new NativeTestDriver(); var clock = new NativeTestClock();
        await using var channel = new NativeNotificationChannel(driver, clock);
        channel.ApplySavedPreferences(NativeNotificationFixture.Preferences()); await channel.ReadStatusAsync();
        driver.PrepareCallback = _ => new NativePrepared(() => { entered.Set(); release.Wait(); return new NativeShowResult(42); });
        var blocking = channel.SendTestAsync();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var queued = channel.SendAsync(NativeNotificationFixture.Submission(expires ? NativeNotificationFixture.Now.AddSeconds(20) : null), default);
            clock.Set(NativeNotificationFixture.Now.AddSeconds(elapsedSeconds)); release.Set();
            Assert.Equal(BatchNoSubmissionReason.ValidityChanged, (await queued).NoSubmissionReason);
            Assert.Equal(BatchChannelOutcome.OsAccepted, (await blocking).Result.Outcome);
            Assert.Single(driver.Payloads);
        }
        finally { release.Set(); await blocking; }
    }

    [Fact]
    public async Task LateCancellationAndDisposeOwnTheActualShowAndBusyReadinessNeverWaits()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var driver = new NativeTestDriver(); using var cancel = new CancellationTokenSource();
        var channel = new NativeNotificationChannel(driver, new NativeTestClock());
        channel.ApplySavedPreferences(NativeNotificationFixture.Preferences()); await channel.ReadStatusAsync();
        driver.PrepareCallback = _ => new NativePrepared(() => { entered.Set(); release.Wait(); return new NativeShowResult(73); });
        var actual = channel.SendAsync(NativeNotificationFixture.Submission(), cancel.Token);
        Task? stop = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(BatchChannelReadiness.Initializing, await channel.ReadReadinessAsync(default).WaitAsync(TimeSpan.FromSeconds(1)));
            cancel.Cancel(); stop = channel.DisposeAsync().AsTask();
            Assert.False(actual.IsCompleted); Assert.False(stop.IsCompleted);
            release.Set(); Assert.Equal(BatchChannelOutcome.OsAccepted, (await actual).Outcome); await stop;
            await Assert.ThrowsAsync<ObjectDisposedException>(() => channel.ReadStatusAsync());
        }
        finally { release.Set(); await actual; await (stop ?? channel.DisposeAsync().AsTask()); }
    }

    [Fact]
    public async Task QueuedCancelAndPermissionChangeProveNoShow()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var driver = new NativeTestDriver();
        await using var channel = new NativeNotificationChannel(driver, new NativeTestClock());
        channel.ApplySavedPreferences(NativeNotificationFixture.Preferences()); await channel.ReadStatusAsync();
        driver.PrepareCallback = _ => new NativePrepared(() => { entered.Set(); release.Wait(); return new NativeShowResult(42); });
        var first = channel.SendTestAsync(); using var cancel = new CancellationTokenSource();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var queued = channel.SendAsync(NativeNotificationFixture.Submission(), cancel.Token); cancel.Cancel();
            driver.Readiness = BatchChannelReadiness.Disabled; release.Set();
            Assert.Equal(BatchNoSubmissionReason.Cancelled, (await queued).NoSubmissionReason);
            Assert.Equal(BatchNoSubmissionReason.ChannelNotReady, (await channel.SendAsync(NativeNotificationFixture.Submission(), default)).NoSubmissionReason);
            Assert.Single(driver.Payloads);
        }
        finally { release.Set(); await first; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreparationFailureIsNoSubmissionButEnteredShowFailureIsUnknown(bool prepareFails)
    {
        var driver = new NativeTestDriver();
        await using var channel = new NativeNotificationChannel(driver, new NativeTestClock());
        channel.ApplySavedPreferences(NativeNotificationFixture.Preferences()); await channel.ReadStatusAsync();
        driver.PrepareCallback = _ => prepareFails ? throw new ArgumentException("private data") : new NativePrepared(() => throw new InvalidOperationException("private data"));
        var result = await channel.SendAsync(NativeNotificationFixture.Submission(), default);
        Assert.Equal(prepareFails ? BatchChannelOutcome.NotSubmitted : BatchChannelOutcome.AcceptanceUnknown, result.Outcome);
        Assert.Equal(prepareFails ? BatchNoSubmissionReason.PayloadRejected : null, result.NoSubmissionReason);
        Assert.NotEqual(BatchChannelReadiness.Ready, await channel.ReadReadinessAsync(default));
    }
}
