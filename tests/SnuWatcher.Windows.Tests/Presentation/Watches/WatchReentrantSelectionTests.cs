using System.ComponentModel;
using SnuWatcher.Application.Presentation.Reads;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Watches;

public sealed class WatchReentrantSelectionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationObserverSelectingAnotherWatchCannotCompleteItsHeldRead(bool firstFails)
    {
        await using var fixture = new WatchPresentationFixture();
        var first = fixture.Add(1); var second = fixture.Add(2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<WatchDetailsSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ReadDetails = (id, _) =>
        {
            if (id == first.Subscription.Id)
                return firstFails ? Task.FromException<WatchDetailsSnapshot?>(new IOException("private first read path"))
                    : Task.FromResult<WatchDetailsSnapshot?>(first);
            entered.TrySetResult();
            return release.Task;
        };
        Task? selectingSecond = null;
        void OnPublished(object? sender, PropertyChangedEventArgs args)
        {
            var state = fixture.ViewModel.State;
            if (selectingSecond is not null || state.SelectedId != first.Subscription.Id || state.SelectionPending) return;
            selectingSecond = Task.CompletedTask; // Guard the nested synchronous PropertyChanged call.
            selectingSecond = fixture.ViewModel.SelectWatchAsync(second.Subscription.Id);
        }
        fixture.ViewModel.PropertyChanged += OnPublished;
        var selectingFirst = fixture.ViewModel.SelectWatchAsync(first.Subscription.Id);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Assert.NotNull(selectingSecond);
            Assert.True(fixture.ViewModel.State.SelectionPending);
            Assert.False(selectingSecond.IsCompleted);
        }
        finally
        {
            fixture.ViewModel.PropertyChanged -= OnPublished;
            release.TrySetResult(second);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selectingFirst);
            if (selectingSecond is not null) await selectingSecond.WaitAsync(Deadline);
        }
        Assert.False(fixture.ViewModel.State.SelectionPending);
        Assert.Equal(second.Subscription.Id, fixture.ViewModel.State.Details!.Source.Subscription.Id);
    }
}
