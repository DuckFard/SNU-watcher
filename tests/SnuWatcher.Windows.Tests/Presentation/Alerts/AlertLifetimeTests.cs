using System.ComponentModel;
using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Windows.Presentation.Alerts;
using SnuWatcher.Windows.Presentation.Common;
using SnuWatcher.Windows.Tests.Presentation.Common;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Alerts;

public sealed class AlertLifetimeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task ReentrantDisposeWithRealFlowingExecutionContextDoesNotSelfAwaitItsPublishingCommand()
    {
        await using var dispatcher = new FlowDispatcher();
        var model = new AlertHistoryViewModel(new HistoryReader(), dispatcher, TimeProvider.System);
        Task? closing = null;
        model.Model.PropertyChanged += (_, _) =>
        {
            if (model.State.IsLoaded && closing is null) closing = model.DisposeAsync().AsTask();
        };
        try
        {
            await model.LoadCommand.ExecuteAsync().WaitAsync(Timeout);
            Assert.NotNull(closing);
            await closing!.WaitAsync(Timeout);
            Assert.Same(closing, model.DisposeAsync().AsTask());
            Assert.False(model.LoadCommand.CanExecute(null));
        }
        finally { await model.DisposeAsync(); }
    }

    [Fact]
    public async Task ClosingDispatcherCannotReleaseAnActualBlockedDesktopActionBeforeItStops()
    {
        await using var dispatcher = new FlowDispatcher();
        var actions = new RecordingActions();
        var model = new AlertHistoryViewModel(new HistoryReader(), dispatcher, TimeProvider.System, actions: actions);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        actions.CopyHandler = (_, _) => { entered.TrySetResult(); return release.Task; };
        try
        {
            await model.LoadCommand.ExecuteAsync();
            await model.SelectEventAsync(AlertFixture.Id(1));
            var copy = model.CopyIdentifierCommand.ExecuteAsync();
            await entered.Task.WaitAsync(Timeout);
            dispatcher.Reject = true;
            var closing = model.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            release.TrySetResult();
            await copy.WaitAsync(Timeout);
            await closing.WaitAsync(Timeout);
        }
        finally { release.TrySetResult(); await model.DisposeAsync(); }
    }

    [Fact]
    public async Task CancelledSelectionWaiterDetachesWithoutCancellingTheOwnedRead()
    {
        await using var f = new AlertFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AlertHistoryItem?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken driverToken = default;
        f.Reader.Details = (_, token) => { driverToken = token; entered.TrySetResult(); return release.Task; };
        await f.Model.LoadCommand.ExecuteAsync();
        f.Model.Model.PropertyChanged += (_, _) => { if (f.Model.State.Details is not null) published.TrySetResult(); };
        using var caller = new CancellationTokenSource();
        try
        {
            var selection = f.Model.SelectEventAsync(AlertFixture.Id(1), caller.Token);
            await entered.Task.WaitAsync(Timeout);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection);
            Assert.False(driverToken.IsCancellationRequested);
            release.TrySetResult(AlertFixture.Item());
            await published.Task.WaitAsync(Timeout);
        }
        finally { release.TrySetResult(AlertFixture.Item()); }
    }

    [Fact]
    public async Task FailedUiPublicationIsSanitizedAndAllOwnedWorkStillDisposes()
    {
        await using var f = new AlertFixture();
        f.Ui.FailNextInvoke();
        Assert.Equal(UiCommandOutcome.Failed, await f.Model.LoadCommand.ExecuteAsync());
        Assert.All(f.Errors, code => Assert.Equal("ui_alert_history_failed", code));
        await f.Model.DisposeAsync();
    }

    [Fact]
    public async Task CancellationObserverReentrantDisposeRetainsExactlyOneTaskUntilTheActualReadStops()
    {
        await using var dispatcher = new FlowDispatcher();
        var reader = new HistoryReader();
        var eventId = AlertFixture.Id(1);
        var recipient = new AlertHistoryRecipient(AlertFixture.Id(2), 1, SnuWatcher.Core.OpeningReason.IncreasedSeats, "Current label");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AlertRecipientPage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.Recipients = (id, after, _) =>
        {
            if (after is not null) { entered.TrySetResult(); return release.Task; }
            return Task.FromResult<AlertRecipientPage?>(new(id, AlertFixture.Stamp, [recipient], true, new(id, recipient.SubscriptionId, 1)));
        };
        var model = new AlertHistoryViewModel(reader, dispatcher, TimeProvider.System);
        Task? closing = null;
        Task? reentrant = null;
        var once = false;
        PropertyChangedEventHandler observer = (_, _) =>
        {
            if (!once && model.RecipientNextCommand.IsCancellationRequested)
            { once = true; reentrant = model.DisposeAsync().AsTask(); }
        };
        try
        {
            await model.LoadCommand.ExecuteAsync();
            await model.SelectEventAsync(eventId);
            var next = model.RecipientNextCommand.ExecuteAsync();
            await entered.Task.WaitAsync(Timeout);
            model.RecipientNextCommand.PropertyChanged += observer;
            await dispatcher.InvokeAsync(() => closing = model.DisposeAsync().AsTask());
            Assert.NotNull(reentrant);
            Assert.Same(closing, reentrant);
            Assert.False(closing!.IsCompleted);
            release.TrySetResult(null);
            await next.WaitAsync(Timeout);
            await closing.WaitAsync(Timeout);
        }
        finally
        {
            model.RecipientNextCommand.PropertyChanged -= observer;
            release.TrySetResult(null);
            if (reentrant is not null) await reentrant.WaitAsync(Timeout);
            await model.DisposeAsync();
        }
    }

    private sealed class FlowDispatcher : IUiDispatcher, IAsyncDisposable
    {
        private readonly DedicatedUiDispatcher inner = new();
        internal bool Reject { get; set; }
        public bool CheckAccess() => inner.CheckAccess();
        public void Post(Action action)
        {
            if (Reject) throw new InvalidOperationException("private closing dispatcher");
            var context = ExecutionContext.Capture();
            inner.Post(() => Run(context, action));
        }
        public Task InvokeAsync(Action action, CancellationToken token = default)
        {
            if (Reject) return Task.FromException(new InvalidOperationException("private closing dispatcher"));
            var context = ExecutionContext.Capture();
            return inner.InvokeAsync(() => Run(context, action), token);
        }
        private static void Run(ExecutionContext? context, Action action)
        { if (context is null) action(); else ExecutionContext.Run(context, _ => action(), null); }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
