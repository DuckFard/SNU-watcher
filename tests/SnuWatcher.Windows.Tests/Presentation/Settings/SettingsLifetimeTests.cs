using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Windows.Presentation.Common;
using SnuWatcher.Windows.Presentation.Settings;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Settings;

public sealed class SettingsLifetimeTests
{
    [Fact]
    public async Task FailedHintReadHasAnExplicitLocalErrorWithoutSavingOrApplying()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        fixture.Store.ReadHandler = _ => throw new IOException("private local read");
        var pending = await fixture.ViewModel.PersistTermHintAsync(2026, "21");
        Assert.Equal(SettingsHintStatus.Pending, pending.Status);
        Assert.Equal("Ui.CommandFailed", fixture.ViewModel.Model.State.IssueKey);
        Assert.Empty(fixture.Store.Commands);
        Assert.Empty(fixture.Profile.Calls);
    }

    [Fact]
    public async Task ReloadFailurePreservesBothSavedStateAndUnsavedEdits()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.EditAsync();
        var stored = fixture.ViewModel.Model.State.Stored;
        fixture.Store.ReadHandler = _ => throw new IOException("private local read");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ViewModel.LoadAsync());
        Assert.Equal("settings_load_failed", failure.Message);
        Assert.Same(stored, fixture.ViewModel.Model.State.Stored);
        Assert.Equal(UiLanguage.Korean, fixture.ViewModel.Language);
        Assert.True(fixture.ViewModel.Model.State.IsDirty);
    }

    [Fact]
    public async Task ReloadCannotForgetTheOriginalUncertainSave()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.EditAsync();
        fixture.Store.SaveHandler = (_, _) => throw new IOException("unknown save");
        await fixture.ViewModel.SaveCommand.ExecuteAsync();
        await fixture.ViewModel.LoadAsync();
        Assert.True(fixture.ViewModel.Model.State.SaveUncertain);
        fixture.Store.SaveHandler = null;
        await fixture.ViewModel.SaveCommand.ExecuteAsync();
        Assert.Same(fixture.Store.Commands[0], fixture.Store.Commands[1]);
    }

    [Fact]
    public async Task CancelAfterTheKnownSaveLeavesHonestPendingApplication()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.EditAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Profile.Handler = async (_, _, token) =>
        { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException(); };
        var saving = fixture.ViewModel.SaveCommand.ExecuteAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.ViewModel.SaveCommand.Cancel();
        Assert.Equal(UiCommandOutcome.Canceled, await saving.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, fixture.ViewModel.Model.State.Stored!.Revision);
        Assert.True(fixture.ViewModel.Model.State.IsApplyPending);
        Assert.False(fixture.ViewModel.Model.State.SaveUncertain);
    }

    [Fact]
    public async Task DisposalDrainsTheActualUncooperativeHintWriteAndClosesAdmission()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Store.SaveHandler = async (command, _) =>
        { entered.SetResult(); await release.Task; return fixture.Store.Commit(command); };
        var hint = fixture.ViewModel.PersistTermHintAsync(2026, "21");
        Task disposing;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            disposing = fixture.ViewModel.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hint);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.ViewModel.PersistTermHintAsync(2027, "21"));
        }
        finally { release.TrySetResult(); }
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Store.Current.Revision);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.ViewModel.LoadAsync());
        Assert.Equal(UiCommandOutcome.Unavailable, await fixture.ViewModel.SaveCommand.ExecuteAsync());
    }

    [Fact]
    public async Task EditsValidateEnumsAndRequireTheUiDispatcher()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        Assert.Throws<InvalidOperationException>(() => fixture.ViewModel.Language = UiLanguage.Korean);
        await fixture.Dispatcher.InvokeAsync(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => fixture.ViewModel.Language = (UiLanguage)99);
            Assert.Throws<ArgumentOutOfRangeException>(() => fixture.ViewModel.DesiredProfile = (WatchMonitorProfile)99);
            Assert.Throws<ArgumentOutOfRangeException>(() => fixture.ViewModel.CloseBehavior = (UiCloseBehavior)99);
        });
        Assert.False(fixture.ViewModel.Model.State.IsDirty);
        Assert.Empty(fixture.Store.Commands);
    }

    [Fact]
    public async Task ObserverFailuresCannotPreventTheSaveOrOtherSubscribers()
    {
        await using var fixture = new SettingsFixture();
        var notified = 0;
        fixture.ViewModel.PreferencesChanged += _ => throw new InvalidOperationException("private observer");
        fixture.ViewModel.PreferencesChanged += _ => notified++;
        fixture.ViewModel.PropertyChanged += (_, _) => throw new InvalidOperationException("private observer");
        await fixture.ViewModel.LoadAsync();
        notified = 0;
        await fixture.EditAsync();
        Assert.Equal(UiCommandOutcome.Completed, await fixture.ViewModel.SaveCommand.ExecuteAsync());
        Assert.Equal(1, notified);
        Assert.Single(fixture.Profile.Calls);
        Assert.Contains("ui_settings_observer_failed", fixture.Errors);
        Assert.All(fixture.Errors, value => Assert.DoesNotContain("private", value));
    }

    [Fact]
    public async Task AnUnchangedExplicitSaveAcknowledgesTheNewPreferencesRevision()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        Assert.Equal(UiCommandOutcome.Completed, await fixture.ViewModel.SaveCommand.ExecuteAsync());
        Assert.Equal(WatchMonitorProfile.Normal, Assert.Single(fixture.Profile.Calls).Profile);
        Assert.Equal(1, fixture.ViewModel.Model.State.Applied!.PreferencesRevision);
        Assert.False(fixture.ViewModel.Model.State.IsDirty);
    }

    [Fact]
    public async Task ANewerActualProfileIsDisplayedWithoutClaimingTheOlderDesiredRevisionWasApplied()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.EditAsync();
        fixture.Profile.Handler = (_, _, _) => Task.FromResult(SettingsProfile.Snapshot(WatchMonitorProfile.LowActivity, 9));
        await fixture.ViewModel.SaveCommand.ExecuteAsync();
        Assert.Equal(1, fixture.ViewModel.Model.State.Stored!.Revision);
        Assert.Equal(9, fixture.ViewModel.Model.State.Applied!.PreferencesRevision);
        Assert.Equal(WatchMonitorProfile.LowActivity, fixture.ViewModel.Model.State.Applied.Profile);
        Assert.True(fixture.ViewModel.Model.State.IsApplyPending);
    }

    [Fact]
    public async Task AForeignReceiptCannotApplyOrReplaceTheStableSaveFacts()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.EditAsync();
        fixture.Store.SaveHandler = (command, _) =>
        {
            var foreign = new UiPreferencesSnapshot(command.ExpectedRevision + 1, command.Values, SettingsClock.Now);
            return Task.FromResult(new UiPreferencesSaveResult(UiPreferencesSaveStatus.Applied,
                new(Guid.NewGuid(), command.ExpectedRevision, foreign), foreign));
        };
        Assert.Equal(UiCommandOutcome.Failed, await fixture.ViewModel.SaveCommand.ExecuteAsync());
        Assert.True(fixture.ViewModel.Model.State.SaveUncertain);
        Assert.Empty(fixture.Profile.Calls);
        fixture.Store.SaveHandler = null;
        await fixture.ViewModel.SaveCommand.ExecuteAsync();
        Assert.Same(fixture.Store.Commands[0], fixture.Store.Commands[1]);
    }

    [Fact]
    public async Task TypedReplayConflictIsExplicitRatherThanAnUncertainOrAppliedSave()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.EditAsync();
        fixture.Store.SaveHandler = (_, _) => throw new UiPreferencesReplayConflictException();
        Assert.Equal(UiCommandOutcome.Completed, await fixture.ViewModel.SaveCommand.ExecuteAsync());
        Assert.True(fixture.ViewModel.Model.State.HasConflict);
        Assert.False(fixture.ViewModel.Model.State.SaveUncertain);
        Assert.Empty(fixture.Profile.Calls);
    }
}
