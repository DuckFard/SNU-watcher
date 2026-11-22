using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Windows.Notifications;
using Xunit;

namespace SnuWatcher.Windows.Tests.Notifications;

public sealed class NativeChannelEdgeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(129)]
    public void QueueBoundsAreExplicit(int limit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeNotificationChannel(new NativeTestDriver(), maxPending: limit));

    [Fact]
    public void MissingDriverIsRejected() => Assert.Throws<ArgumentNullException>(() => new NativeNotificationChannel(null!));

    [Fact]
    public async Task LegacyTestPreservesM2PayloadWithoutPromotingUnconfiguredProduction()
    {
        var driver = new NativeTestDriver();
        await using var channel = new NativeNotificationChannel(driver, new NativeTestClock());
        var receipt = await channel.SendLegacyTestAsync();
        Assert.Equal(BatchChannelOutcome.OsAccepted, receipt.Result.Outcome);
        var payload = Assert.Single(driver.Payloads);
        Assert.Equal(RegistrationNotificationPayload.CreateTest(), payload.Xml);
        Assert.Equal("native-test", payload.Tag); Assert.Equal("feasibility", payload.Group);
        Assert.Equal(BatchChannelReadiness.Initializing, await channel.ReadReadinessAsync(default));
        Assert.Null(payload.ExpiresAtUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PrepareTimeChangesAreRecheckedBeforeTheShowBoundary(int change)
    {
        var driver = new NativeTestDriver(); var clock = new NativeTestClock(); using var cancel = new CancellationTokenSource();
        await using var channel = new NativeNotificationChannel(driver, clock);
        channel.ApplySavedPreferences(NativeNotificationFixture.Preferences()); await channel.ReadStatusAsync();
        var shows = 0;
        driver.PrepareCallback = _ =>
        {
            if (change == 0) driver.Readiness = BatchChannelReadiness.Disabled;
            if (change == 1) clock.Set(NativeNotificationFixture.Now.AddMinutes(3));
            if (change == 2) cancel.Cancel();
            return new NativePrepared(() => { shows++; return new NativeShowResult(42); });
        };
        var result = await channel.SendAsync(NativeNotificationFixture.Submission(), cancel.Token);
        Assert.Equal(change == 0 ? BatchNoSubmissionReason.ChannelNotReady : change == 1
            ? BatchNoSubmissionReason.ValidityChanged : BatchNoSubmissionReason.Cancelled, result.NoSubmissionReason);
        Assert.Equal(0, shows);
    }

    [Theory]
    [InlineData(false, BatchChannelOutcome.AcceptanceUnknown)]
    [InlineData(true, BatchChannelOutcome.RetryableFailure)]
    public async Task ZeroIdNeedsPositiveRejectionProofBeforeItCanBeRetryable(bool rejection, BatchChannelOutcome expected)
    {
        var driver = new NativeTestDriver { PrepareCallback = _ => new NativePrepared(() => new NativeShowResult(0, rejection)) };
        await using var channel = new NativeNotificationChannel(driver, new NativeTestClock());
        channel.ApplySavedPreferences(NativeNotificationFixture.Preferences());
        Assert.Equal(expected, (await channel.SendTestAsync()).Result.Outcome);
        Assert.Throws<ArgumentException>(() => new NativeShowResult(42, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProbeFailureAndInvalidReadinessAreSanitizedAndNeverPrepare(bool throws)
    {
        var diagnostics = new List<string>();
        var driver = new NativeTestDriver { ProbeCallback = () => throws ? throw new InvalidOperationException("secret") : (BatchChannelReadiness)999 };
        await using var channel = new NativeNotificationChannel(driver, new NativeTestClock(), diagnostic: x => diagnostics.Add(x));
        channel.ApplySavedPreferences(NativeNotificationFixture.Preferences());
        Assert.Equal(BatchChannelReadiness.Unavailable, (await channel.ReadStatusAsync()).Readiness);
        Assert.Equal(BatchNoSubmissionReason.ChannelNotReady, (await channel.SendAsync(NativeNotificationFixture.Submission(), default)).NoSubmissionReason);
        Assert.Empty(driver.Payloads); Assert.DoesNotContain(diagnostics, x => x.Contains("secret"));
    }

    [Fact]
    public async Task QueueIsBoundedAndStopRefusesQueuedShowsWhileDrainingActualOne()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var driver = new NativeTestDriver { PrepareCallback = _ => new NativePrepared(() => { entered.Set(); release.Wait(); return new NativeShowResult(42); }) };
        var channel = new NativeNotificationChannel(driver, new NativeTestClock(), 1);
        channel.ApplySavedPreferences(NativeNotificationFixture.Preferences()); await channel.ReadStatusAsync();
        var first = channel.SendTestAsync(); Task<NativeNotificationTestReceipt>? queued = null; Task? stop = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            queued = channel.SendTestAsync();
            Assert.Equal(BatchNoSubmissionReason.ChannelNotReady, (await channel.SendAsync(NativeNotificationFixture.Submission(), default)).NoSubmissionReason);
            await Assert.ThrowsAsync<InvalidOperationException>(() => channel.ReadStatusAsync());
            stop = channel.DisposeAsync().AsTask();
            Assert.False(stop.IsCompleted); release.Set(); await first;
            Assert.Equal(BatchNoSubmissionReason.Cancelled, (await queued).Result.NoSubmissionReason); await stop;
            Assert.Single(driver.Payloads);
        }
        finally { release.Set(); await first; if (queued is not null) await queued; await (stop ?? channel.DisposeAsync().AsTask()); }
    }

    [Fact]
    public async Task CancelledProbeAndSendDoNotInvokeDriverAndDisposalIsIdempotent()
    {
        var driver = new NativeTestDriver(); var channel = new NativeNotificationChannel(driver, new NativeTestClock());
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.ReadStatusAsync(cancel.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.ReadReadinessAsync(cancel.Token));
            Assert.Equal(BatchNoSubmissionReason.Cancelled, (await channel.SendAsync(NativeNotificationFixture.Submission(), cancel.Token)).NoSubmissionReason);
            Assert.Throws<ArgumentNullException>(() => channel.ApplySavedPreferences(null!));
            Assert.Throws<ArgumentNullException>(() => { _ = channel.SendAsync(null!, default); });
            Assert.Empty(driver.Payloads);
        }
        finally { await channel.DisposeAsync(); await channel.DisposeAsync(); }
        Assert.Throws<ObjectDisposedException>(() => channel.ApplySavedPreferences(NativeNotificationFixture.Preferences()));
        Assert.Equal(BatchChannelReadiness.Unavailable, await channel.ReadReadinessAsync(default));
    }

    [Fact]
    public async Task ActualSdkWorkStaysOnOneMtaThreadAndObserversCannotEraseAcceptance()
    {
        var ids = new List<int>(); var apartments = new List<ApartmentState>(); var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Record() { ids.Add(Environment.CurrentManagedThreadId); apartments.Add(Thread.CurrentThread.GetApartmentState()); }
        var driver = new NativeTestDriver
        {
            ProbeCallback = () => { Record(); return BatchChannelReadiness.Ready; },
            PrepareCallback = _ => { Record(); return new NativePrepared(() => { Record(); return new NativeShowResult(19); }, () => throw new InvalidOperationException("private")); }
        };
        await using var channel = new NativeNotificationChannel(driver, new NativeTestClock(), diagnostic: code => { diagnostics.Enqueue(code); throw new Exception(); });
        channel.AvailabilityChanged += () => throw new Exception("private");
        channel.ApplySavedPreferences(NativeNotificationFixture.Preferences()); await channel.ReadStatusAsync();
        var result = await channel.SendTestAsync();
        Assert.Equal(BatchChannelOutcome.OsAccepted, result.Result.Outcome);
        Assert.Single(ids.Distinct()); Assert.All(apartments, state => Assert.Equal(ApartmentState.MTA, state));
        Assert.Contains("native_object_cleanup_failed", diagnostics); Assert.Contains("native_availability_observer_failed", diagnostics);
        Assert.DoesNotContain(diagnostics, code => code.Contains("private"));
    }

    [Fact]
    public async Task CompletedProbeDoesNotWakeMerelyBecauseItsTimestampChanged()
    {
        var channel = new NativeNotificationChannel(new NativeTestDriver(), new NativeTestClock());
        var events = 0; channel.AvailabilityChanged += () => Interlocked.Increment(ref events);
        try
        {
            // Absent configuration remains Initializing throughout probes: no meaningful transition.
            await channel.ReadStatusAsync(); await channel.ReadStatusAsync();
            Assert.Equal(0, Volatile.Read(ref events));
        }
        finally { await channel.DisposeAsync(); }
    }

    [Fact]
    public async Task DisposalAfterReadinessIsProvenNoAdmissionRatherThanChargedUnknown()
    {
        var driver = new NativeTestDriver(); await using var channel = new NativeNotificationChannel(driver, new NativeTestClock());
        channel.ApplySavedPreferences(NativeNotificationFixture.Preferences()); await channel.ReadStatusAsync();
        await NativeNotificationFixture.EventuallyAsync(() => channel.ReadReadinessAsync(default).GetAwaiter().GetResult() == BatchChannelReadiness.Ready);
        await channel.DisposeAsync();
        var result = await channel.SendAsync(NativeNotificationFixture.Submission(), default);
        Assert.Equal(BatchChannelOutcome.NotSubmitted, result.Outcome);
        Assert.Equal(BatchNoSubmissionReason.ChannelNotReady, result.NoSubmissionReason);
        Assert.Equal(BatchChannelOutcome.NotSubmitted, (await channel.SendTestAsync()).Result.Outcome);
        Assert.Empty(driver.Payloads);
    }
}
