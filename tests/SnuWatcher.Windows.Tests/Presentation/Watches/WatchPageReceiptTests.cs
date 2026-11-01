using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Windows.Presentation.Common;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Watches;

public sealed class WatchPageReceiptTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task OnlySuccessfulPublishedReadsAdvanceReceiptsAndNavigationCannotProveReload()
    {
        await using var fixture = new WatchPresentationFixture { RowsPerPage = 1 };
        fixture.Add(1); fixture.Add(2);
        var viewModel = fixture.ViewModel;
        Assert.Equal(0, viewModel.SuccessfulPageReadVersion);
        Assert.Equal(0, viewModel.SuccessfulReloadVersion);
        await viewModel.ReloadCommand.ExecuteAsync();
        Assert.Equal(1, viewModel.SuccessfulPageReadVersion);
        Assert.Equal(1, viewModel.SuccessfulReloadVersion);
        await viewModel.NextCommand.ExecuteAsync();
        Assert.Equal(2, viewModel.SuccessfulPageReadVersion);
        Assert.Equal(1, viewModel.SuccessfulReloadVersion);
        await fixture.Dispatcher.InvokeAsync(viewModel.RefreshAges);
        await viewModel.SelectWatchAsync(WatchPresentationFixture.Id(2));
        Assert.Equal(2, viewModel.SuccessfulPageReadVersion);
        Assert.Equal(1, viewModel.SuccessfulReloadVersion);
        fixture.FailReads = true;
        await viewModel.ReloadCommand.ExecuteAsync();
        Assert.Equal("Ui.CommandFailed", viewModel.State.MessageKey);
        Assert.Equal(2, viewModel.SuccessfulPageReadVersion);
        Assert.Equal(1, viewModel.SuccessfulReloadVersion);
        fixture.FailReads = false;
        fixture.AppliedTarget = TimeSpan.Zero; // Projection rejects an invalid applied profile.
        await viewModel.ReloadCommand.ExecuteAsync();
        Assert.Equal("Ui.CommandFailed", viewModel.State.MessageKey);
        Assert.Equal(2, viewModel.SuccessfulPageReadVersion);
        Assert.Equal(1, viewModel.SuccessfulReloadVersion);
    }

    [Fact]
    public async Task CompetingNextSuccessDoesNotAcknowledgeQueuedReloadFailure()
    {
        await using var fixture = new WatchPresentationFixture { RowsPerPage = 1 };
        fixture.Add(1); fixture.Add(2);
        var viewModel = fixture.ViewModel;
        await viewModel.ReloadCommand.ExecuteAsync();
        var page = await fixture.ReadWatchPageAsync(new(1, WatchPresentationFixture.Id(1)));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        fixture.ReadPage = async (cursor, _) =>
        {
            if (cursor is null) throw new IOException("private reload failure");
            if (Interlocked.Increment(ref calls) != 1) throw new IOException("private reload failure");
            entered.TrySetResult();
            await release.Task;
            return page;
        };
        var beforeReads = fixture.PageReads;
        var next = viewModel.NextCommand.ExecuteAsync();
        Task<UiCommandOutcome>? reload = null;
        try
        {
            await entered.Task.WaitAsync(Deadline);
            reload = viewModel.ReloadCommand.ExecuteAsync();
            Assert.True(viewModel.ReloadCommand.IsExecuting);
            Assert.False(reload.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await next.WaitAsync(Deadline);
        await reload!.WaitAsync(Deadline);
        Assert.Equal(beforeReads + 2, fixture.PageReads);
        Assert.Equal(2, viewModel.SuccessfulPageReadVersion);
        Assert.Equal(1, viewModel.SuccessfulReloadVersion);
        Assert.Equal("Ui.CommandFailed", viewModel.State.MessageKey);
    }

    [Fact]
    public async Task FailedPagePublicationAndMutationReadCannotForgeExplicitReloadReceipt()
    {
        await using var fixture = new WatchPresentationFixture();
        fixture.Add(1);
        var viewModel = fixture.ViewModel;
        await viewModel.SelectWatchAsync(WatchPresentationFixture.Id(1));
        await viewModel.PauseCommand.ExecuteAsync();
        Assert.Equal(2, viewModel.SuccessfulPageReadVersion);
        Assert.Equal(1, viewModel.SuccessfulReloadVersion);
        var page = await fixture.ReadWatchPageAsync();
        fixture.ReadPage = (_, _) =>
        {
            fixture.Dispatcher.FailNextInvoke();
            return Task.FromResult(page);
        };
        await viewModel.ReloadCommand.ExecuteAsync();
        Assert.Equal("Ui.CommandFailed", viewModel.State.MessageKey);
        Assert.Equal(2, viewModel.SuccessfulPageReadVersion);
        Assert.Equal(1, viewModel.SuccessfulReloadVersion);
    }
}
