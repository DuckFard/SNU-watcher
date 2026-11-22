using SnuWatcher.Windows.Presentation.Common;
using SnuWatcher.Windows.Presentation.Watches;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Watches;

public sealed class WatchLifetimeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task DispatcherRejectionCannotSkipActualReadAndCancellationCallbackDrain()
    {
        await using var fixture = new WatchPresentationFixture();
        fixture.Add(1);
        var page = await fixture.ReadWatchPageAsync();
        var dispatcher = new RejectingDispatcher(fixture.Dispatcher);
        var viewModel = new WatchListViewModel(fixture, fixture, dispatcher, fixture.Clock, () => fixture.AppliedTarget);
        var entered = Barrier(); var release = Barrier();
        var cancellationEntered = Barrier(); var cancellationRelease = Barrier();
        fixture.ReadPage = async (_, token) =>
        {
            using var registration = token.Register(() =>
            {
                cancellationEntered.TrySetResult();
                cancellationRelease.Task.GetAwaiter().GetResult();
            });
            entered.TrySetResult();
            await release.Task; // The reader deliberately ignores cancellation.
            return page;
        };
        var reading = viewModel.ReloadCommand.ExecuteAsync();
        try
        {
            await entered.Task.WaitAsync(Deadline);
            dispatcher.Reject = true;
            var disposing = viewModel.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted);
            Assert.Equal(UiCommandOutcome.Unavailable, await viewModel.ReloadCommand.ExecuteAsync());
            await cancellationEntered.Task.WaitAsync(Deadline);
            release.TrySetResult();
            Assert.False(disposing.IsCompleted);
            cancellationRelease.TrySetResult();
            await disposing.WaitAsync(Deadline);
            Assert.True(reading.IsCompleted);
        }
        finally
        {
            release.TrySetResult(); cancellationRelease.TrySetResult();
            await reading.WaitAsync(Deadline);
            await viewModel.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    private static TaskCompletionSource Barrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class RejectingDispatcher(IUiDispatcher inner) : IUiDispatcher
    {
        internal volatile bool Reject;
        public bool CheckAccess() => inner.CheckAccess();
        public void Post(Action action)
        {
            if (Reject) throw new InvalidOperationException("private dispatcher shutdown path");
            inner.Post(action);
        }
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) => Reject
            ? Task.FromException(new InvalidOperationException("private dispatcher shutdown path"))
            : inner.InvokeAsync(action, cancellationToken);
    }
}
