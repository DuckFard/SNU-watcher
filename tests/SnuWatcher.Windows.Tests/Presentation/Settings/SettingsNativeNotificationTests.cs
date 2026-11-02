using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Windows.Notifications;
using SnuWatcher.Windows.Presentation.Common;
using SnuWatcher.Windows.Presentation.Settings;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Settings;

public sealed class SettingsNativeNotificationTests
{
    [Fact]
    public async Task LoadReadsReadinessWithoutSendingOrSavingAndExplicitRefreshCanRecover()
    {
        await using var fixture = new SettingsFixture();
        var controls = new Controls { Readiness = BatchChannelReadiness.Disabled };
        await using var model = Create(fixture, controls);
        await model.LoadAsync();
        Assert.Equal(BatchChannelReadiness.Disabled, model.Model.State.NativeStatus!.Readiness);
        Assert.False(model.NativeTestCommand.CanExecute(null));
        Assert.Empty(fixture.Store.Commands); Assert.Equal(0, controls.Tests);
        controls.Readiness = BatchChannelReadiness.Ready;
        Assert.Equal(UiCommandOutcome.Completed, await model.RefreshNativeStatusCommand.ExecuteAsync());
        Assert.True(model.NativeTestCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(BatchChannelReadiness.Initializing)]
    [InlineData(BatchChannelReadiness.Disabled)]
    [InlineData(BatchChannelReadiness.Unavailable)]
    public async Task ABlockedNativeStatusNeverEnablesTesting(BatchChannelReadiness readiness)
    {
        await using var fixture = new SettingsFixture(); var controls = new Controls { Readiness = readiness };
        await using var model = Create(fixture, controls); await model.LoadAsync();
        Assert.Equal(UiCommandOutcome.Unavailable, await model.NativeTestCommand.ExecuteAsync());
        Assert.Equal(0, controls.Tests);
    }

    [Fact]
    public async Task AcceptedIsNotSeenAndConfirmationCanOnlyBelongToTheCurrentAcceptedTest()
    {
        await using var fixture = new SettingsFixture(); var controls = new Controls();
        await using var model = Create(fixture, controls); await model.LoadAsync();
        await model.NativeTestCommand.ExecuteAsync();
        var first = model.Model.State.NativeTestReceipt!.TestId;
        Assert.Equal(NativeTestBannerConfirmation.Unknown, model.Model.State.NativeTestFeedback!.Confirmation);
        await fixture.Dispatcher.InvokeAsync(() => Assert.True(model.ConfirmNativeTest(first, NativeTestBannerConfirmation.Seen)));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controls.Test = async _ => { entered.SetResult(); await release.Task; return Accepted(); };
        var next = model.NativeTestCommand.ExecuteAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(model.Model.State.NativeTestReceipt); Assert.Null(model.Model.State.NativeTestFeedback);
            await fixture.Dispatcher.InvokeAsync(() => Assert.False(model.ConfirmNativeTest(first, NativeTestBannerConfirmation.Seen)));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(UiCommandOutcome.Completed, await next.WaitAsync(TimeSpan.FromSeconds(5)));
        var second = model.Model.State.NativeTestReceipt!.TestId; Assert.NotEqual(first, second);
        await fixture.Dispatcher.InvokeAsync(() =>
        {
            Assert.False(model.ConfirmNativeTest(first, NativeTestBannerConfirmation.Seen));
            Assert.True(model.ConfirmNativeTest(second, NativeTestBannerConfirmation.NotSeen));
        });
        Assert.Equal(second, model.Model.State.NativeTestFeedback!.TestId);
        Assert.Equal(NativeTestBannerConfirmation.NotSeen, model.Model.State.NativeTestFeedback.Confirmation);
    }

    [Theory]
    [InlineData(BatchChannelOutcome.NotSubmitted)]
    [InlineData(BatchChannelOutcome.RetryableFailure)]
    [InlineData(BatchChannelOutcome.AcceptanceUnknown)]
    public async Task HumanConfirmationIsUnavailableForUnacceptedTests(BatchChannelOutcome outcome)
    {
        await using var fixture = new SettingsFixture();
        var controls = new Controls { Test = _ => Task.FromResult(new NativeNotificationTestReceipt(Guid.NewGuid(),
            new(outcome, outcome == BatchChannelOutcome.NotSubmitted ? BatchNoSubmissionReason.ChannelNotReady : null), null, SettingsClock.Now)) };
        await using var model = Create(fixture, controls); await model.LoadAsync(); await model.NativeTestCommand.ExecuteAsync();
        Assert.Null(model.Model.State.NativeTestFeedback);
        await fixture.Dispatcher.InvokeAsync(() => Assert.False(model.ConfirmNativeTest(model.Model.State.NativeTestReceipt!.TestId,
            NativeTestBannerConfirmation.Seen)));
    }

    [Fact]
    public async Task ActualNativeWorkDoesNotHoldThePreferenceSaveLaneAndDisposalWaitsForIt()
    {
        await using var fixture = new SettingsFixture(); var controls = new Controls();
        await using var model = Create(fixture, controls); await model.LoadAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controls.Test = async token =>
        { using var registration = token.Register(() => canceled.TrySetResult()); entered.SetResult(); await release.Task; return Accepted(); };
        var testing = model.NativeTestCommand.ExecuteAsync(); Task? disposing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(model.Model.State.IsNativeBusy);
            await fixture.Dispatcher.InvokeAsync(() => model.NotificationSoundMuted = true);
            Assert.Equal(UiCommandOutcome.Completed, await model.SaveCommand.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(Assert.Single(fixture.Store.Commands).Values.NotificationSoundMuted);
            disposing = model.DisposeAsync().AsTask();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(disposing.IsCompleted);
            Assert.False(model.NativeTestCommand.CanExecute(null));
        }
        finally { release.TrySetResult(); }
        await testing.WaitAsync(TimeSpan.FromSeconds(5));
        if (disposing is not null) await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BatchChannelOutcome.OsAccepted, model.Model.State.NativeTestReceipt!.Result.Outcome);
    }

    [Fact]
    public async Task ReentrantStopDuringNativeBusyPublicationDoesNotReachThePort()
    {
        await using var fixture = new SettingsFixture(); var controls = new Controls();
        await using var model = Create(fixture, controls, new FlowingDispatcher(fixture.Dispatcher)); await model.LoadAsync();
        Task? disposal = null;
        model.Model.PropertyChanged += (_, _) =>
        {
            if (!model.Model.State.IsNativeBusy || disposal is not null) return;
            disposal = model.DisposeAsync().AsTask();
            Assert.False(model.NativeTestCommand.CanExecute(null));
            Assert.False(model.RefreshNativeStatusCommand.CanExecute(null));
        };
        await model.NativeTestCommand.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(disposal); await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, controls.Tests);
    }

    [Fact]
    public async Task RequestedCancellationWithoutAReceiptDoesNotKeepAnEarlierHumanConfirmation()
    {
        await using var fixture = new SettingsFixture(); var controls = new Controls();
        await using var model = Create(fixture, controls); await model.LoadAsync();
        await model.NativeTestCommand.ExecuteAsync();
        var old = model.Model.State.NativeTestReceipt!.TestId;
        await fixture.Dispatcher.InvokeAsync(() => model.ConfirmNativeTest(old, NativeTestBannerConfirmation.Seen));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controls.Test = async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Accepted(); };
        var testing = model.NativeTestCommand.ExecuteAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); model.NativeTestCommand.Cancel();
        Assert.Equal(UiCommandOutcome.Canceled, await testing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(model.Model.State.NativeTestReceipt); Assert.Null(model.Model.State.NativeTestFeedback);
        Assert.Equal("Ui.NativeTestCanceled", model.Model.State.NativeIssueKey);
    }

    [Fact]
    public async Task SuccessfulRefreshClearsAProbeFailureWithoutErasingTheActualTestReceipt()
    {
        await using var fixture = new SettingsFixture(); var controls = new Controls();
        await using var model = Create(fixture, controls); await model.LoadAsync();
        controls.Test = _ => { controls.StatusFailure = true; return Task.FromResult(Accepted()); };
        await model.NativeTestCommand.ExecuteAsync();
        var receipt = model.Model.State.NativeTestReceipt;
        Assert.NotNull(receipt); Assert.Null(model.Model.State.NativeStatus);
        Assert.Equal("Ui.NativeStatusUnavailable", model.Model.State.NativeIssueKey);
        controls.StatusFailure = false;
        Assert.Equal(UiCommandOutcome.Completed, await model.RefreshNativeStatusCommand.ExecuteAsync());
        Assert.Same(receipt, model.Model.State.NativeTestReceipt);
        Assert.Equal(BatchChannelReadiness.Ready, model.Model.State.NativeStatus!.Readiness);
        Assert.Equal("Ui.NativeTestAccepted", model.Model.State.NativeIssueKey);
    }

    [Fact]
    public async Task StatusAndTestFailuresExposeOnlyFixedKeysAndCanBeRetried()
    {
        await using var fixture = new SettingsFixture(); var controls = new Controls { StatusFailure = true };
        await using var model = Create(fixture, controls);
        Assert.Equal(UiCommandOutcome.Failed, await model.RefreshNativeStatusCommand.ExecuteAsync());
        Assert.Equal("Ui.NativeStatusUnavailable", model.Model.State.NativeIssueKey); Assert.Null(model.Model.State.NativeStatus);
        controls.StatusFailure = false; await model.RefreshNativeStatusCommand.ExecuteAsync();
        await model.NativeTestCommand.ExecuteAsync();
        var old = model.Model.State.NativeTestReceipt!.TestId;
        await fixture.Dispatcher.InvokeAsync(() => model.ConfirmNativeTest(old, NativeTestBannerConfirmation.Seen));
        controls.Test = _ => throw new IOException("private SDK details");
        Assert.Equal(UiCommandOutcome.Failed, await model.NativeTestCommand.ExecuteAsync());
        Assert.Equal("Ui.NativeTestFailed", model.Model.State.NativeIssueKey);
        Assert.Null(model.Model.State.NativeTestReceipt); Assert.Null(model.Model.State.NativeTestFeedback);
        Assert.All(fixture.Errors, error => Assert.DoesNotContain("private", error));
    }

    private static SettingsViewModel Create(SettingsFixture fixture, Controls controls, IUiDispatcher? dispatcher = null) =>
        new(fixture.Store, fixture.Profile, dispatcher ?? fixture.Dispatcher, new SettingsClock(), reportError: fixture.Errors.Add, nativeNotifications: controls);
    private static NativeNotificationTestReceipt Accepted() => new(Guid.NewGuid(), new(BatchChannelOutcome.OsAccepted), 42, SettingsClock.Now);

    private sealed class Controls : INativeNotificationControls
    {
        internal BatchChannelReadiness Readiness { get; set; } = BatchChannelReadiness.Ready;
        internal bool StatusFailure { get; set; }
        internal int Tests { get; private set; }
        internal Func<CancellationToken, Task<NativeNotificationTestReceipt>> Test { get; set; } = _ => Task.FromResult(Accepted());
        public Task<NativeNotificationStatus> ReadStatusAsync(CancellationToken cancellationToken = default) => StatusFailure
            ? throw new IOException("private SDK details") : Task.FromResult(new NativeNotificationStatus(Readiness, Readiness == BatchChannelReadiness.Ready, SettingsClock.Now));
        public Task<NativeNotificationTestReceipt> SendTestAsync(CancellationToken cancellationToken = default)
        { Tests++; return Test(cancellationToken); }
    }

    private sealed class FlowingDispatcher(IUiDispatcher inner) : IUiDispatcher
    {
        public bool CheckAccess() => inner.CheckAccess();
        public void Post(Action action) => inner.Post(Capture(action));
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) =>
            inner.InvokeAsync(Capture(action), cancellationToken);
        private static Action Capture(Action action)
        {
            var context = ExecutionContext.Capture();
            return () =>
            {
                if (context is null) action();
                else ExecutionContext.Run(context, _ => action(), null);
            };
        }
    }
}
