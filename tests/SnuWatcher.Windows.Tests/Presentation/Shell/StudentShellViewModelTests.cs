using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Windows.Presentation.Common;
using SnuWatcher.Windows.Presentation.Shell;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Shell;

public sealed class StudentShellViewModelTests
{
    [Fact]
    public async Task LocalStartupExposesCachedHintsWithoutAuthorizingSearchOrCallingSource()
    {
        await using var fixture = new ShellFixture(demo: true);
        Assert.Equal(StudentPage.FindCourses, fixture.Shell.SelectedPage);
        Assert.Same(fixture.Search, fixture.Shell.PageContent);
        Assert.Equal(UiCommandOutcome.Completed, await fixture.Shell.LoadInitialAsync());
        Assert.True(fixture.Shell.State.IsLoaded);
        Assert.True(fixture.Shell.State.IsDemo);
        Assert.True(fixture.Shell.State.ShowOnboarding);
        Assert.Same(fixture.Ports.Terms, fixture.Shell.State.TermHints);
        Assert.Null(fixture.Search.SelectedTerm);
        Assert.Empty(fixture.Search.Model.State.Terms);
        Assert.Equal(0, fixture.Ports.HttpCalls);
        Assert.Equal(0, fixture.Ports.PageReads);
        Assert.Equal("Ui.SourceUnknown", fixture.Shell.State.Source.MessageKey);
    }

    [Fact]
    public async Task NavigationReloadsOnlyOwnedLocalChildCommandsAndAlertsAreAnHonestPlaceholder()
    {
        await using var fixture = new ShellFixture();
        await fixture.Shell.LoadInitialAsync();
        await fixture.Dispatcher.InvokeAsync(() => fixture.Shell.Navigate(StudentPage.MyWatches));
        await ShellFixture.EventuallyAsync(() => fixture.Watches.State.IsLoaded);
        Assert.Same(fixture.Watches, fixture.Shell.PageContent);
        Assert.Equal(1, fixture.Ports.PageReads);
        await fixture.Dispatcher.InvokeAsync(() => fixture.Shell.SelectedPage = StudentPage.Alerts);
        Assert.IsType<StudentAlertsPlaceholder>(fixture.Shell.PageContent);
        await fixture.Dispatcher.InvokeAsync(() => fixture.Shell.Navigate(StudentPage.Settings));
        Assert.Same(fixture.Settings, fixture.Shell.PageContent);
        Assert.Equal(0, fixture.Ports.HttpCalls);
        Assert.Throws<InvalidOperationException>(() => fixture.Shell.Navigate(StudentPage.FindCourses));
    }

    [Fact]
    public async Task SourceReadFailureIsFixedSavedStateErrorAndNeverPretendsItIsRemoteOutage()
    {
        await using var fixture = new ShellFixture();
        fixture.Ports.SourceHandler = _ => throw new IOException("private database path");
        await fixture.Shell.LoadInitialAsync();
        Assert.Equal("Ui.SavedStateError", fixture.Shell.State.IssueKey);
        Assert.Equal("Ui.SavedStateError", fixture.Shell.State.Source.MessageKey);
        Assert.Equal(0, fixture.Ports.HttpCalls);
    }

    [Fact]
    public async Task KnownPreferenceSaveUpdatesShellAndForwardsObserversOnUiDispatcher()
    {
        await using var fixture = new ShellFixture();
        await fixture.Shell.LoadInitialAsync();
        var notifications = new List<int>();
        fixture.Shell.PreferencesChanged += _ => throw new IOException("private observer failure");
        fixture.Shell.PreferencesChanged += _ => notifications.Add(Environment.CurrentManagedThreadId);
        await fixture.Dispatcher.InvokeAsync(() =>
        { fixture.Settings.Language = UiLanguage.Korean; fixture.Settings.CloseBehavior = UiCloseBehavior.Quit; });
        Assert.Equal(UiCommandOutcome.Completed, await fixture.Settings.SaveCommand.ExecuteAsync());
        Assert.Equal(UiLanguage.Korean, fixture.Shell.State.Language);
        Assert.Equal(UiCloseBehavior.Quit, fixture.Shell.State.CloseBehavior);
        Assert.All(notifications, thread => Assert.Equal(fixture.Dispatcher.ThreadId, thread));
        Assert.NotEmpty(notifications);
    }

    [Fact]
    public async Task OnboardingSavesKnownEditorBeforeCompletingAndNeverHidesUncertainSave()
    {
        await using var fixture = new ShellFixture();
        await fixture.Shell.LoadInitialAsync();
        await fixture.Dispatcher.InvokeAsync(() => fixture.Settings.Language = UiLanguage.Korean);
        await fixture.Shell.CompleteOnboardingCommand.ExecuteAsync();
        Assert.False(fixture.Shell.State.ShowOnboarding);
        Assert.Equal(UiLanguage.Korean, fixture.Ports.Preferences.Values.Language);
        Assert.Equal(1, fixture.Ports.Preferences.Values.OnboardingVersion);
        Assert.Equal(2, fixture.Ports.Saves.Count);
    }

    [Fact]
    public async Task UncertainEditorSaveCannotCompleteOnboarding()
    {
        await using var fixture = new ShellFixture();
        await fixture.Shell.LoadInitialAsync();
        fixture.Ports.SaveHandler = (_, _) => throw new IOException("private save details");
        await fixture.Dispatcher.InvokeAsync(() => fixture.Settings.Language = UiLanguage.Korean);
        await fixture.Shell.CompleteOnboardingCommand.ExecuteAsync();
        Assert.True(fixture.Shell.State.ShowOnboarding);
        Assert.All(fixture.Ports.Saves, command => Assert.Equal(0, command.Values.OnboardingVersion));
        Assert.Equal("Ui.SettingsPending", fixture.Shell.State.IssueKey);
    }

    [Fact]
    public async Task ActualWatchReceiptRoutesToDirectWatchSelectionWithoutDuplicatePageReload()
    {
        await using var fixture = new ShellFixture();
        await fixture.Shell.LoadInitialAsync();
        await fixture.Search.LoadCatalogCommand.ExecuteAsync();
        await fixture.Dispatcher.InvokeAsync(() =>
        { fixture.Search.SelectedTerm = fixture.Search.Model.State.Terms.Single(); fixture.Search.CourseCode = "F32.102"; });
        await fixture.Search.SearchCommand.ExecuteAsync();
        await fixture.Dispatcher.InvokeAsync(() => fixture.Search.ToggleSection(ShellPorts.Key, true));
        await fixture.Search.WatchSelection.SaveCommand.ExecuteAsync();
        await ShellFixture.EventuallyAsync(() => fixture.Shell.SelectedPage == StudentPage.MyWatches && fixture.Watches.State.Details is not null);
        Assert.Equal(fixture.Ports.Created!.Id, fixture.Watches.State.SelectedId);
        Assert.Single(fixture.Ports.DetailsIds);
        Assert.Equal(1, fixture.Ports.PageReads);
    }

    [Fact]
    public async Task NewWatchRefreshesAnAlreadyLoadedEmptyPageBeforeSelectingItsActualReceipt()
    {
        await using var fixture = new ShellFixture();
        await fixture.Shell.LoadInitialAsync();
        await fixture.Dispatcher.InvokeAsync(() => fixture.Shell.Navigate(StudentPage.MyWatches));
        await ShellFixture.EventuallyAsync(() => fixture.Watches.State.IsLoaded && !fixture.Watches.ReloadCommand.IsExecuting);
        await fixture.Dispatcher.InvokeAsync(() => fixture.Shell.Navigate(StudentPage.FindCourses));
        await fixture.Search.LoadCatalogCommand.ExecuteAsync();
        await fixture.Dispatcher.InvokeAsync(() =>
        { fixture.Search.SelectedTerm = fixture.Search.Model.State.Terms.Single(); fixture.Search.CourseCode = "F32.102"; });
        await fixture.Search.SearchCommand.ExecuteAsync();
        await fixture.Dispatcher.InvokeAsync(() => fixture.Search.ToggleSection(ShellPorts.Key, true));
        await fixture.Search.WatchSelection.SaveCommand.ExecuteAsync();
        await ShellFixture.EventuallyAsync(() => fixture.Watches.State.Details is not null);
        Assert.Equal(2, fixture.Ports.PageReads);
        Assert.Equal(fixture.Ports.Created!.Id, fixture.Watches.State.SelectedId);
    }

    [Fact]
    public async Task ShellStopDrainsIgnoringLocalReadButDoesNotDisposeInjectedChildren()
    {
        await using var fixture = new ShellFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SnuWatcher.Application.Presentation.Reads.SourceStatusSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Ports.SourceHandler = _ => { entered.TrySetResult(); return release.Task; };
        var load = fixture.Shell.LoadInitialAsync();
        Task? stopping = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stopping = fixture.Shell.DisposeAsync().AsTask();
            Assert.False(stopping.IsCompleted);
        }
        finally { release.TrySetResult(fixture.Ports.Source); }
        await stopping!.WaitAsync(TimeSpan.FromSeconds(5));
        await load;
        Assert.Equal(UiCommandOutcome.Unavailable, await fixture.Shell.LoadInitialAsync());
        Assert.Equal(UiCommandOutcome.Completed, await fixture.Search.LoadCatalogCommand.ExecuteAsync());
    }

    [Fact]
    public async Task ExplicitResumeUsesDisplayedPolicyRevisionAndRereadsWithoutInventingSuccess()
    {
        await using var fixture = new ShellFixture();
        var at = fixture.Clock.GetUtcNow().AddMinutes(-1);
        var policy = new SourcePolicySnapshot(SourcePolicyIds.SnuSugang, 11,
            new(1, at, 1, SourceRequestOutcome.AccessRestricted, at, 0, null), new(1, at, "access_restricted"));
        fixture.Ports.Source = new(new(0, 0, at), policy, null);
        await fixture.Shell.LoadInitialAsync();
        Assert.True(fixture.Shell.ResumeAccessCommand.CanExecute(null));
        await fixture.Shell.ResumeAccessCommand.ExecuteAsync();
        Assert.Equal(11, fixture.Ports.ResumedRevision);
        Assert.Equal(2, fixture.Ports.SourceReads);
        Assert.Equal("Ui.SourceRestricted", fixture.Shell.State.Source.MessageKey);
        Assert.Null(fixture.Shell.State.Source.LastSourceSuccessAtUtc);
        Assert.Equal(0, fixture.Ports.HttpCalls);
    }

    [Fact]
    public async Task StatusOnlySnapshotAndAgeTickNeverReloadTheVisibleWatchPage()
    {
        await using var fixture = new ShellFixture(controls: false);
        await fixture.Shell.LoadInitialAsync();
        await fixture.Dispatcher.InvokeAsync(() => fixture.Shell.Navigate(StudentPage.MyWatches));
        await ShellFixture.EventuallyAsync(() => fixture.Watches.State.IsLoaded && !fixture.Watches.ReloadCommand.IsExecuting);
        await fixture.Dispatcher.InvokeAsync(() =>
        { fixture.Shell.UpdateRuntime(ShellPorts.InitialRuntime(), null); fixture.Clock.Advance(TimeSpan.FromMinutes(1)); fixture.Shell.RefreshAges(); });
        Assert.True(fixture.Shell.State.Source.IsOnline);
        Assert.Equal(1, fixture.Ports.PageReads);
        Assert.Equal(1, fixture.Ports.SourceReads);
        Assert.False(fixture.Shell.RecoverPolicyCommand.CanExecute(null));
        Assert.Equal(0, fixture.Ports.HttpCalls);
    }

    [Fact]
    public async Task StickyRuntimeFailureSurvivesEmptyUpdateAndAgeTickUsesNoSourceRead()
    {
        await using var fixture = new ShellFixture();
        await fixture.Shell.LoadInitialAsync();
        var hub = new RuntimeUpdateHub(fixture.Clock);
        hub.ReportFailure(RuntimeFailureKind.SavedState, "source_policy_storage_failed");
        await fixture.Dispatcher.InvokeAsync(() =>
        { fixture.Shell.UpdateRuntime(null, hub.Failure); fixture.Shell.UpdateRuntime(null, null); fixture.Clock.Advance(TimeSpan.FromMinutes(5)); fixture.Shell.RefreshAges(); });
        Assert.Equal("Ui.SavedStateError", fixture.Shell.State.Source.MessageKey);
        Assert.True(fixture.Shell.RecoverPolicyCommand.CanExecute(null));
        Assert.Equal(1, fixture.Ports.SourceReads);
        Assert.Equal(0, fixture.Ports.HttpCalls);
        await fixture.Shell.RecoverPolicyCommand.ExecuteAsync();
        Assert.Equal(1, fixture.Ports.Recoveries);
    }
}
