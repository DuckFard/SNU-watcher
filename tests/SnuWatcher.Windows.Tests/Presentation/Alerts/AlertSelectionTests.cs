using System.ComponentModel;
using SnuWatcher.Application.Presentation.Alerts;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Alerts;

public sealed class AlertSelectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task OldUncooperativeDetailCannotOverwriteNewSelectionAndClearToNullInvalidatesIt()
    {
        await using var f = new AlertFixture();
        f.Reader.Page = (_, _) => Task.FromResult(new AlertHistoryPage(AlertFixture.Stamp, 1000,
            [AlertFixture.Item(1, 1000), AlertFixture.Item(2, 999)], false, null));
        await f.Model.LoadCommand.ExecuteAsync();
        var entered = Signal();
        var release = new TaskCompletionSource<AlertHistoryItem?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Reader.Details = (id, _) => id == AlertFixture.Id(1) ? Block() : Task.FromResult<AlertHistoryItem?>(AlertFixture.Item(2, 999));
        Task<AlertHistoryItem?> Block() { entered.TrySetResult(); return release.Task; }
        var first = f.Model.SelectEventAsync(AlertFixture.Id(1));
        try
        {
            await entered.Task.WaitAsync(Timeout);
            Assert.True(await f.Model.SelectEventAsync(null));
            var second = f.Model.SelectEventAsync(AlertFixture.Id(2));
            release.TrySetResult(AlertFixture.Item(1));
            Assert.True(await second.WaitAsync(Timeout));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.Equal(AlertFixture.Id(2), f.Model.State.Details!.EventId);
        }
        finally { release.TrySetResult(AlertFixture.Item(1)); }
    }

    [Fact]
    public async Task ReentrantSelectionDuringNullPublicationCannotSettleTheNewerGeneration()
    {
        await using var f = new AlertFixture();
        await f.Model.LoadCommand.ExecuteAsync();
        var entered = Signal();
        var release = new TaskCompletionSource<AlertHistoryItem?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Reader.Details = (_, _) => { entered.TrySetResult(); return release.Task; };
        var old = f.Model.SelectEventAsync(AlertFixture.Id(1));
        await entered.Task.WaitAsync(Timeout);
        Task<bool>? reentered = null;
        var once = false;
        PropertyChangedEventHandler observer = (_, _) =>
        {
            if (!once && f.Model.State.SelectedEventId is null)
            { once = true; reentered = f.Model.SelectEventAsync(AlertFixture.Id(1)); }
        };
        f.Model.Model.PropertyChanged += observer;
        try
        {
            var cleared = f.Model.SelectEventAsync(null);
            try { await cleared.WaitAsync(Timeout); } catch (OperationCanceledException) { }
            release.TrySetResult(AlertFixture.Item(1));
            Assert.NotNull(reentered);
            Assert.True(await reentered!.WaitAsync(Timeout));
            Assert.Equal(AlertFixture.Id(1), f.Model.State.Details!.EventId);
            try { await old; } catch (OperationCanceledException) { }
        }
        finally { f.Model.Model.PropertyChanged -= observer; release.TrySetResult(AlertFixture.Item(1)); }
    }

    [Fact]
    public async Task SynchronousSecondPortFailureCannotOrphanAnAlreadyStartedDetailRead()
    {
        var f = new AlertFixture();
        var entered = Signal();
        var release = new TaskCompletionSource<AlertHistoryItem?>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Reader.Details = (_, _) => { entered.TrySetResult(); return release.Task; };
        f.Reader.Recipients = (_, _, _) => throw new IOException("private synchronous read error");
        try
        {
            await f.Model.LoadCommand.ExecuteAsync();
            var selection = f.Model.SelectEventAsync(AlertFixture.Id(1));
            await entered.Task.WaitAsync(Timeout);
            await Assert.ThrowsAsync<TimeoutException>(() => selection.WaitAsync(TimeSpan.FromMilliseconds(250)));
            var closing = f.Model.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            release.TrySetResult(AlertFixture.Item(1));
            await closing.WaitAsync(Timeout);
            try { await selection; } catch (OperationCanceledException) { }
        }
        finally { release.TrySetResult(AlertFixture.Item(1)); await f.DisposeAsync(); }
    }
}
