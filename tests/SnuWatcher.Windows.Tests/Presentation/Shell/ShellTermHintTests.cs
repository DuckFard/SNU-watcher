using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Windows.Presentation.Common;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Shell;

public sealed class ShellTermHintTests
{
    [Fact]
    public async Task ConfirmedTermPersistsOnlySourcePairAndPreservesDirtyLanguageAndProfileEditor()
    {
        await using var fixture = new ShellFixture();
        await fixture.Shell.LoadInitialAsync();
        await fixture.Dispatcher.InvokeAsync(() =>
        { fixture.Settings.Language = UiLanguage.Korean; fixture.Settings.DesiredProfile = WatchMonitorProfile.Faster; });
        await fixture.Search.LoadCatalogCommand.ExecuteAsync();
        await fixture.Dispatcher.InvokeAsync(() => fixture.Search.SelectedTerm = fixture.Search.Model.State.Terms.Single());
        await ShellFixture.EventuallyAsync(() => fixture.Settings.Model.State.Stored?.Values.TermHint is not null && !fixture.Settings.Model.State.IsBusy);
        Assert.Equal(2026, fixture.Ports.Preferences.Values.SelectedYear);
        Assert.Equal("21", fixture.Ports.Preferences.Values.TermHint!.Selector);
        Assert.Equal(UiLanguage.English, fixture.Ports.Preferences.Values.Language);
        Assert.Equal(WatchMonitorProfile.Normal, fixture.Ports.Preferences.Values.DesiredProfile);
        Assert.Equal(UiLanguage.Korean, fixture.Settings.Language);
        Assert.Equal(WatchMonitorProfile.Faster, fixture.Settings.DesiredProfile);
        Assert.True(fixture.Settings.Model.State.IsDirty);
        await fixture.Dispatcher.InvokeAsync(() =>
        { fixture.Search.CourseName = "typed"; fixture.Search.CourseName = "typed again"; fixture.Search.Instructor = "professor"; });
        Assert.Single(fixture.Ports.Saves);
        await fixture.Dispatcher.InvokeAsync(() => fixture.Search.SelectedYear = 2027);
        Assert.Null(fixture.Search.SelectedTerm);
        Assert.Single(fixture.Ports.Saves);
        Assert.Equal(1, fixture.Ports.HttpCalls);
    }

    [Fact]
    public async Task RapidConfirmedTermChangesCoalesceOnlyTheLatestPendingPair()
    {
        await using var fixture = new ShellFixture();
        fixture.Ports.CatalogTerms = [new(2026, "11", "First", null), new(2026, "21", "Second", null), new(2026, "22", "Other", null)];
        await fixture.Shell.LoadInitialAsync();
        await fixture.Search.LoadCatalogCommand.ExecuteAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Ports.SaveHandler = async (command, _) =>
        {
            if (fixture.Ports.Saves.Count == 1) { entered.TrySetResult(); await release.Task; }
            var saved = new UiPreferencesSnapshot(command.ExpectedRevision + 1, command.Values, fixture.Clock.GetUtcNow());
            fixture.Ports.Preferences = saved;
            return new(UiPreferencesSaveStatus.Applied, new(command.CommandId, command.ExpectedRevision, saved), saved);
        };
        try
        {
            await fixture.Dispatcher.InvokeAsync(() => fixture.Search.SelectedTerm = fixture.Search.Model.State.Terms[0]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.Dispatcher.InvokeAsync(() =>
            { fixture.Search.SelectedTerm = fixture.Search.Model.State.Terms[1]; fixture.Search.SelectedTerm = fixture.Search.Model.State.Terms[2]; });
            release.TrySetResult();
            await ShellFixture.EventuallyAsync(() => fixture.Settings.Model.State.Stored?.Values.TermHint?.Selector == "22" && !fixture.Settings.Model.State.IsBusy);
            Assert.Equal(2, fixture.Ports.Saves.Count);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ShellDisposalDrainsAcceptedHintReceiptAndDetachesFurtherSelections()
    {
        await using var fixture = new ShellFixture();
        await fixture.Shell.LoadInitialAsync();
        await fixture.Search.LoadCatalogCommand.ExecuteAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Ports.SaveHandler = async (command, _) =>
        {
            entered.TrySetResult(); await release.Task;
            var saved = new UiPreferencesSnapshot(command.ExpectedRevision + 1, command.Values, fixture.Clock.GetUtcNow());
            fixture.Ports.Preferences = saved;
            return new(UiPreferencesSaveStatus.Applied, new(command.CommandId, command.ExpectedRevision, saved), saved);
        };
        Task? stopping = null;
        try
        {
            await fixture.Dispatcher.InvokeAsync(() => fixture.Search.SelectedTerm = fixture.Search.Model.State.Terms.Single());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stopping = fixture.Shell.DisposeAsync().AsTask();
            Assert.False(stopping.IsCompleted);
            await fixture.Dispatcher.InvokeAsync(() => fixture.Search.SelectedYear = 2027);
        }
        finally { release.TrySetResult(); }
        await stopping!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(fixture.Ports.Saves);
        Assert.Equal("21", fixture.Ports.Preferences.Values.TermHint!.Selector);
    }

    [Fact]
    public async Task PendingHintIsVisibleAndTypingDoesNotBlindlyRetryTheSamePreferenceCas()
    {
        await using var fixture = new ShellFixture();
        await fixture.Shell.LoadInitialAsync();
        fixture.Ports.SaveHandler = (_, _) => throw new IOException("private hint write");
        await fixture.Search.LoadCatalogCommand.ExecuteAsync();
        await fixture.Dispatcher.InvokeAsync(() => fixture.Search.SelectedTerm = fixture.Search.Model.State.Terms.Single());
        await ShellFixture.EventuallyAsync(() => fixture.Shell.State.IssueKey == "Ui.SettingsPending");
        var count = fixture.Ports.Saves.Count;
        await fixture.Dispatcher.InvokeAsync(() =>
        { fixture.Search.CourseCode = "F32.102"; fixture.Search.CourseCode = "F35.105"; });
        Assert.Equal(count, fixture.Ports.Saves.Count);
        Assert.Null(fixture.Ports.Preferences.Values.TermHint);
    }
}
