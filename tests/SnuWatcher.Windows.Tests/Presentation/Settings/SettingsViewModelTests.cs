using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Windows.Presentation.Common;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Settings;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task LoadingDefaultsReadsOnlyLocalStateWithoutSavingOrApplying()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        Assert.Equal(UiLanguage.English, fixture.ViewModel.Language);
        Assert.Equal(WatchMonitorProfile.Normal, fixture.ViewModel.DesiredProfile);
        Assert.Equal(UiCloseBehavior.MinimizeToTray, fixture.ViewModel.CloseBehavior);
        Assert.True(fixture.ViewModel.Model.State.IsLoaded);
        Assert.False(fixture.ViewModel.Model.State.IsDirty);
        Assert.False(fixture.ViewModel.Model.State.IsApplyPending);
        Assert.Empty(fixture.Store.Commands);
        Assert.Empty(fixture.Profile.Calls);
    }

    [Fact]
    public async Task SavePersistsIntentAndPublishesSavedPreferencesBeforeApplyingTheProfile()
    {
        await using var fixture = new SettingsFixture();
        var order = new List<string>();
        fixture.Store.Saved = _ => order.Add("save");
        fixture.Profile.Applying = () => order.Add("apply");
        fixture.ViewModel.PreferencesChanged += _ =>
        { Assert.True(fixture.Dispatcher.CheckAccess()); order.Add("saved-event"); };
        await fixture.ViewModel.LoadAsync();
        order.Clear();
        await fixture.EditAsync();
        Assert.Equal(UiCommandOutcome.Completed, await fixture.ViewModel.SaveCommand.ExecuteAsync());
        Assert.Equal(new[] { "save", "saved-event", "apply" }, order);
        var saved = Assert.Single(fixture.Store.Commands);
        Assert.Equal(UiLanguage.Korean, saved.Values.Language);
        Assert.Equal(WatchMonitorProfile.Faster, saved.Values.DesiredProfile);
        Assert.Equal(UiCloseBehavior.Quit, saved.Values.CloseBehavior);
        Assert.Equal(1, fixture.ViewModel.Model.State.Stored!.Revision);
        Assert.Equal(1, fixture.ViewModel.Model.State.Applied!.PreferencesRevision);
        Assert.False(fixture.ViewModel.Model.State.IsDirty);
        Assert.False(fixture.ViewModel.Model.State.IsApplyPending);
    }

    [Fact]
    public async Task AnAmbiguousSaveRetriesTheIdenticalCommandBeforeApplyingAnything()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.EditAsync();
        fixture.Store.SaveHandler = (_, _) => throw new IOException("private database detail");
        Assert.Equal(UiCommandOutcome.Failed, await fixture.ViewModel.SaveCommand.ExecuteAsync());
        Assert.True(fixture.ViewModel.Model.State.SaveUncertain);
        Assert.Empty(fixture.Profile.Calls);
        Assert.False(fixture.ViewModel.DiscardChangesCommand.CanExecute(null));
        fixture.Store.SaveHandler = null;
        Assert.Equal(UiCommandOutcome.Completed, await fixture.ViewModel.SaveCommand.ExecuteAsync());
        Assert.Same(fixture.Store.Commands[0], fixture.Store.Commands[1]);
        Assert.False(fixture.ViewModel.Model.State.SaveUncertain);
        Assert.Single(fixture.Profile.Calls);
        Assert.All(fixture.Errors, error => Assert.DoesNotContain("private", error));
    }

    [Fact]
    public async Task AReplayedOldReceiptAppliesItsCurrentSnapshotInsteadOfItsOriginalSavedValues()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.EditAsync();
        fixture.Store.SaveHandler = (command, _) =>
        {
            fixture.Store.Commit(command);
            fixture.Store.ExternalSave(new(UiLanguage.English, null, null, WatchMonitorProfile.LowActivity, UiCloseBehavior.MinimizeToTray, 0));
            throw new IOException("lost accepted return");
        };
        Assert.Equal(UiCommandOutcome.Failed, await fixture.ViewModel.SaveCommand.ExecuteAsync());
        fixture.Store.SaveHandler = null;
        Assert.Equal(UiCommandOutcome.Completed, await fixture.ViewModel.SaveCommand.ExecuteAsync());
        Assert.Same(fixture.Store.Commands[0], fixture.Store.Commands[1]);
        var applied = Assert.Single(fixture.Profile.Calls);
        Assert.Equal(2, applied.Revision);
        Assert.Equal(WatchMonitorProfile.LowActivity, applied.Profile);
        Assert.Equal(UiLanguage.English, fixture.ViewModel.Language);
        Assert.Equal(2, fixture.ViewModel.Model.State.Stored!.Revision);
    }

    [Fact]
    public async Task ConflictPreservesDirtyEditsAndTheNextExplicitSaveUsesTheCurrentRevision()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.EditAsync();
        fixture.Store.ExternalSave(new(UiLanguage.English, 2027, new(2027, "reported"), WatchMonitorProfile.Normal, UiCloseBehavior.MinimizeToTray, 0));
        Assert.Equal(UiCommandOutcome.Completed, await fixture.ViewModel.SaveCommand.ExecuteAsync());
        Assert.True(fixture.ViewModel.Model.State.HasConflict);
        Assert.Equal(UiLanguage.Korean, fixture.ViewModel.Language);
        Assert.Equal(1, fixture.ViewModel.Model.State.Stored!.Revision);
        Assert.Empty(fixture.Profile.Calls);
        Assert.Equal(UiCommandOutcome.Completed, await fixture.ViewModel.SaveCommand.ExecuteAsync());
        Assert.NotEqual(fixture.Store.Commands[0].CommandId, fixture.Store.Commands[1].CommandId);
        Assert.Equal(1, fixture.Store.Commands[1].ExpectedRevision);
        Assert.Equal("reported", fixture.Store.Commands[1].Values.TermHint!.Selector);
        Assert.False(fixture.ViewModel.Model.State.HasConflict);
    }

    [Fact]
    public async Task ReloadKeepsUnsavedEditsUntilTheStudentExplicitlyDiscardsThem()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.EditAsync();
        fixture.Store.ExternalSave(new(UiLanguage.English, null, null, WatchMonitorProfile.LowActivity, UiCloseBehavior.MinimizeToTray, 0));
        await fixture.ViewModel.LoadAsync();
        Assert.True(fixture.ViewModel.Model.State.IsDirty);
        Assert.Equal(UiLanguage.Korean, fixture.ViewModel.Language);
        Assert.Equal(WatchMonitorProfile.Faster, fixture.ViewModel.DesiredProfile);
        await fixture.ViewModel.DiscardChangesCommand.ExecuteAsync();
        Assert.Equal(UiLanguage.English, fixture.ViewModel.Language);
        Assert.Equal(WatchMonitorProfile.LowActivity, fixture.ViewModel.DesiredProfile);
        Assert.False(fixture.ViewModel.Model.State.IsDirty);
    }

    [Fact]
    public async Task ApplyFailureKeepsKnownSavedIntentAndRetryDoesNotSaveAgain()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.EditAsync();
        fixture.Profile.Handler = (_, _, _) => throw new IOException("private actor detail");
        Assert.Equal(UiCommandOutcome.Completed, await fixture.ViewModel.SaveCommand.ExecuteAsync());
        Assert.True(fixture.ViewModel.Model.State.IsApplyPending);
        Assert.False(fixture.ViewModel.Model.State.SaveUncertain);
        Assert.Equal(1, fixture.ViewModel.Model.State.Stored!.Revision);
        fixture.Profile.Handler = null;
        Assert.Equal(UiCommandOutcome.Completed, await fixture.ViewModel.RetryApplyCommand.ExecuteAsync());
        Assert.Single(fixture.Store.Commands);
        Assert.False(fixture.ViewModel.Model.State.IsApplyPending);
        Assert.Equal(1, fixture.ViewModel.Model.State.Applied!.PreferencesRevision);
    }

    [Fact]
    public async Task EditsMadeWhileASaveRunsRemainUnsavedAfterThatOriginalSaveCompletes()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.EditAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Store.SaveHandler = async (command, token) =>
        { entered.SetResult(); await release.Task.WaitAsync(token); return fixture.Store.Commit(command); };
        var saving = fixture.ViewModel.SaveCommand.ExecuteAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.EditAsync(UiLanguage.English, WatchMonitorProfile.LowActivity);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(UiCommandOutcome.Completed, await saving);
        Assert.True(fixture.ViewModel.Model.State.IsDirty);
        Assert.Equal(UiLanguage.English, fixture.ViewModel.Language);
        Assert.Equal(WatchMonitorProfile.LowActivity, fixture.ViewModel.DesiredProfile);
        Assert.Equal(WatchMonitorProfile.Faster, fixture.ViewModel.Model.State.Stored!.Values.DesiredProfile);
    }

    [Fact]
    public async Task NativeTestIsOnlyTheProvidedDispatcherCallback()
    {
        var requested = 0;
        await using var fixture = new SettingsFixture(() => Interlocked.Increment(ref requested));
        Assert.Equal(UiCommandOutcome.Completed, await fixture.ViewModel.NativeTestCommand.ExecuteAsync());
        Assert.Equal(1, requested);
        Assert.Empty(fixture.Store.Commands);
        Assert.Empty(fixture.Profile.Calls);
        await using var unavailable = new SettingsFixture();
        Assert.Equal(UiCommandOutcome.Unavailable, await unavailable.ViewModel.NativeTestCommand.ExecuteAsync());
    }
}
