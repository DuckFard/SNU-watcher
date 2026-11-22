using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Windows.Presentation.Shell;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Shell;

public sealed class ShellCommittedRefreshTests
{
    [Fact]
    public async Task SuccessfulCompetingNextPageCannotAcknowledgeAFailedReload()
    {
        await using var fixture = new ShellFixture();
        var id = Guid.NewGuid();
        var stamp = new UiReadStamp(1, 0, fixture.Clock.GetUtcNow());
        var row = new WatchDefinitionSummary(id, 1, 1, SubscriptionKind.Section, "통계학", WatchStatus.Active,
            false, null, fixture.Clock.GetUtcNow(), new(ShellPorts.Key), 1, SectionSnapshot.Unchecked(ShellPorts.Key));
        var firstPage = new WatchPageReadResult(WatchPageReadDisposition.Page, 1, new(stamp, [row], true, new(1, id)));
        var emptyPage = new WatchPageReadResult(WatchPageReadDisposition.Page, 1, new(stamp, [], false, null));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<WatchPageReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Ports.PageHandler = _ => fixture.Ports.PageReads switch
        {
            1 => Task.FromResult(firstPage),
            2 => BlockedNext(),
            _ => throw new IOException("private failed reload"),
        };
        Task<WatchPageReadResult> BlockedNext() { entered.TrySetResult(); return release.Task; }
        await fixture.Shell.LoadInitialAsync();
        await fixture.Dispatcher.InvokeAsync(() => fixture.Shell.Navigate(StudentPage.MyWatches));
        await ShellFixture.EventuallyAsync(() => fixture.Watches.State.HasNext && !fixture.Watches.ReloadCommand.IsExecuting);
        var reloadBefore = fixture.Watches.SuccessfulReloadVersion;
        var pageBefore = fixture.Watches.SuccessfulPageReadVersion;
        var next = fixture.Watches.NextCommand.ExecuteAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Dispatcher.InvokeAsync(() => fixture.Shell.UpdateRuntime(null, null, 9));
        }
        finally { release.TrySetResult(emptyPage); }
        await next;
        await ShellFixture.EventuallyAsync(() => fixture.Shell.State.IssueKey == "Ui.CommandFailed");
        Assert.True(fixture.Watches.SuccessfulPageReadVersion > pageBefore);
        Assert.Equal(reloadBefore, fixture.Watches.SuccessfulReloadVersion);
        fixture.Ports.PageHandler = _ => Task.FromResult(emptyPage);
        await fixture.Dispatcher.InvokeAsync(() => fixture.Shell.UpdateRuntime(null, null, 9));
        await ShellFixture.EventuallyAsync(() => fixture.Watches.SuccessfulReloadVersion > reloadBefore && fixture.Shell.State.IssueKey is null);
        Assert.Equal(4, fixture.Ports.PageReads);
    }

    [Fact]
    public async Task FailedReadDoesNotConsumeDirtyHintAndSameGenerationCanRetryWithoutStatusPolling()
    {
        await using var fixture = new ShellFixture();
        await fixture.Shell.LoadInitialAsync();
        fixture.Ports.PageHandler = _ => throw new IOException("private read path");
        await fixture.Dispatcher.InvokeAsync(() =>
        { fixture.Shell.UpdateRuntime(null, null, 4); fixture.Shell.Navigate(StudentPage.MyWatches); });
        await ShellFixture.EventuallyAsync(() => fixture.Ports.PageReads == 1 && fixture.Shell.State.IssueKey == "Ui.CommandFailed");
        Assert.Equal("Ui.CommandFailed", fixture.Shell.State.IssueKey);
        Assert.NotEqual("Ui.SavedStateError", fixture.Shell.State.Source.MessageKey);
        fixture.Ports.PageHandler = null;
        await fixture.Dispatcher.InvokeAsync(() => fixture.Shell.UpdateRuntime(null, null));
        Assert.Equal(1, fixture.Ports.PageReads);
        await fixture.Dispatcher.InvokeAsync(() => fixture.Shell.UpdateRuntime(null, null, 4));
        await ShellFixture.EventuallyAsync(() => fixture.Ports.PageReads == 2 && fixture.Watches.State.IsLoaded && fixture.Shell.State.IssueKey is null);
        Assert.Null(fixture.Shell.State.IssueKey);
        Assert.Equal(0, fixture.Ports.HttpCalls);
    }

    [Fact]
    public async Task HiddenCommitHintsCoalesceUntilVisibleAndStatusOrAgeUpdatesDoNotReadWatches()
    {
        await using var fixture = new ShellFixture();
        await fixture.Shell.LoadInitialAsync();
        await fixture.Dispatcher.InvokeAsync(() =>
        {
            fixture.Shell.UpdateRuntime(null, null, 1); fixture.Shell.UpdateRuntime(null, null, 4);
            fixture.Shell.RefreshAges();
        });
        Assert.Equal(0, fixture.Ports.PageReads);
        await fixture.Dispatcher.InvokeAsync(() => fixture.Shell.Navigate(StudentPage.MyWatches));
        await ShellFixture.EventuallyAsync(() => fixture.Watches.State.IsLoaded && !fixture.Watches.ReloadCommand.IsExecuting);
        Assert.Equal(1, fixture.Ports.PageReads);
        await fixture.Dispatcher.InvokeAsync(() =>
        { fixture.Shell.UpdateRuntime(null, null, 4); fixture.Shell.UpdateRuntime(null, null); fixture.Shell.RefreshAges(); });
        Assert.Equal(1, fixture.Ports.PageReads);
    }

    [Fact]
    public async Task CommitDuringOwnedReloadProducesOneLatestFollowupAndDoesNotLoseDirtyGeneration()
    {
        await using var fixture = new ShellFixture();
        await fixture.Shell.LoadInitialAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<WatchPageReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Ports.PageHandler = _ =>
        { if (fixture.Ports.PageReads == 1) { entered.TrySetResult(); return release.Task; } return Task.FromResult(ShellPorts.EmptyPage()); };
        try
        {
            await fixture.Dispatcher.InvokeAsync(() => fixture.Shell.Navigate(StudentPage.MyWatches));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Dispatcher.InvokeAsync(() =>
            { fixture.Shell.UpdateRuntime(null, null, 1); fixture.Shell.UpdateRuntime(null, null, 2); fixture.Shell.UpdateRuntime(null, null, 3); });
        }
        finally { release.TrySetResult(ShellPorts.EmptyPage()); }
        await ShellFixture.EventuallyAsync(() => fixture.Ports.PageReads == 2 && !fixture.Watches.ReloadCommand.IsExecuting);
        await fixture.Dispatcher.InvokeAsync(() => fixture.Shell.UpdateRuntime(null, null, 3));
        Assert.Equal(2, fixture.Ports.PageReads);
    }

    [Fact]
    public async Task BusyChildReloadCannotDiscardTheShellsCommittedRefreshRequest()
    {
        await using var fixture = new ShellFixture();
        await fixture.Shell.LoadInitialAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<WatchPageReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Ports.PageHandler = _ =>
        { if (fixture.Ports.PageReads == 1) { entered.TrySetResult(); return release.Task; } return Task.FromResult(ShellPorts.EmptyPage()); };
        var external = fixture.Watches.ReloadCommand.ExecuteAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Dispatcher.InvokeAsync(() =>
            { fixture.Shell.UpdateRuntime(null, null, 7); fixture.Shell.Navigate(StudentPage.MyWatches); });
            Assert.True(fixture.Watches.ReloadCommand.IsExecuting);
        }
        finally { release.TrySetResult(ShellPorts.EmptyPage()); }
        await external;
        await ShellFixture.EventuallyAsync(() => fixture.Ports.PageReads == 2 && !fixture.Watches.ReloadCommand.IsExecuting);
        Assert.Equal(2, fixture.Ports.PageReads);
    }
}
