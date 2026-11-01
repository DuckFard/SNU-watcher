using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Windows.Presentation.Settings;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Settings;

public sealed class SettingsRecoveryTests
{
    [Fact]
    public async Task PublicationFailureRetainsTheOriginalKnownWriteForReplay()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.EditAsync();
        fixture.Store.SaveHandler = (command, _) =>
        {
            var committed = fixture.Store.Commit(command);
            fixture.Dispatcher.FailNextInvoke();
            return Task.FromResult(committed);
        };
        await fixture.ViewModel.SaveCommand.ExecuteAsync();
        Assert.True(fixture.ViewModel.Model.State.SaveUncertain);
        Assert.Equal(1, fixture.Store.Current.Revision);
        fixture.Store.SaveHandler = null;
        await fixture.ViewModel.SaveCommand.ExecuteAsync();
        Assert.Same(fixture.Store.Commands[0], fixture.Store.Commands[1]);
        Assert.False(fixture.ViewModel.Model.State.SaveUncertain);
        Assert.False(fixture.ViewModel.Model.State.IsApplyPending);
        Assert.Equal(1, fixture.ViewModel.Model.State.Stored!.Revision);
    }

    [Fact]
    public async Task UnknownHintSaveReplaysTheOriginalCommandBeforeSavingTheNewLatestHint()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.EditAsync();
        fixture.Store.SaveHandler = (command, _) => { fixture.Store.Commit(command); throw new IOException("lost hint receipt"); };
        Assert.Equal(SettingsHintStatus.Pending, (await fixture.ViewModel.PersistTermHintAsync(2026, "21")).Status);
        fixture.Store.SaveHandler = null;
        var latest = await fixture.ViewModel.PersistTermHintAsync(2027, "reported");
        Assert.Equal(SettingsHintStatus.Saved, latest.Status);
        Assert.Same(fixture.Store.Commands[0], fixture.Store.Commands[1]);
        Assert.Equal(3, fixture.Store.Commands.Length);
        Assert.Equal(2027, latest.CurrentSnapshot.Values.SelectedYear);
        Assert.Equal("reported", latest.CurrentSnapshot.Values.TermHint!.Selector);
        Assert.Equal(UiLanguage.English, latest.CurrentSnapshot.Values.Language);
        Assert.Equal(UiLanguage.Korean, fixture.ViewModel.Language);
        Assert.True(fixture.ViewModel.Model.State.IsDirty);
    }

    [Fact]
    public async Task AHintReplayConflictDoesNotSilentlySaveANewerHint()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        fixture.Store.SaveHandler = (_, _) => throw new IOException("unknown hint write");
        await fixture.ViewModel.PersistTermHintAsync(2026, "21");
        fixture.Store.SaveHandler = null;
        fixture.Store.ExternalSave(new(UiLanguage.Korean, null, null, WatchMonitorProfile.LowActivity, UiCloseBehavior.Quit, 1));
        var conflict = await fixture.ViewModel.PersistTermHintAsync(2027, "reported");
        Assert.Equal(SettingsHintStatus.Conflict, conflict.Status);
        Assert.Same(fixture.Store.Commands[0], fixture.Store.Commands[1]);
        Assert.Equal(2, fixture.Store.Commands.Length);
        Assert.Null(fixture.Store.Current.Values.TermHint);
        Assert.Empty(fixture.Profile.Calls);
    }

    [Fact]
    public async Task OnboardingConflictRequiresExplicitRetryAndPreservesOtherCurrentValues()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        fixture.Store.SaveHandler = (command, _) =>
        {
            fixture.Store.ExternalSave(new(UiLanguage.Korean, 2027, new(2027, "reported"), WatchMonitorProfile.LowActivity, UiCloseBehavior.Quit, 0));
            return Task.FromResult(fixture.Store.Commit(command));
        };
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ViewModel.CompleteOnboardingAsync(1));
        Assert.Equal("settings_onboarding_pending", failure.Message);
        Assert.True(fixture.ViewModel.Model.State.HasConflict);
        Assert.Empty(fixture.Profile.Calls);
        fixture.Store.SaveHandler = null;
        var saved = await fixture.ViewModel.CompleteOnboardingAsync(1);
        Assert.Equal(1, saved.Values.OnboardingVersion);
        Assert.Equal(UiLanguage.Korean, saved.Values.Language);
        Assert.Equal("reported", saved.Values.TermHint!.Selector);
        Assert.Equal(WatchMonitorProfile.LowActivity, saved.Values.DesiredProfile);
    }

    [Fact]
    public async Task AlreadyCompletedOnboardingDoesNotDecreaseItsVersionOrSaveAgain()
    {
        await using var fixture = new SettingsFixture();
        fixture.Store.ExternalSave(new(UiLanguage.Korean, null, null, WatchMonitorProfile.LowActivity, UiCloseBehavior.Quit, 2));
        await fixture.ViewModel.LoadAsync();
        var existing = fixture.Store.Current;
        Assert.Same(existing, await fixture.ViewModel.CompleteOnboardingAsync(1));
        Assert.Empty(fixture.Store.Commands);
        Assert.Equal(2, fixture.ViewModel.Model.State.Stored!.Values.OnboardingVersion);
    }

    [Fact]
    public async Task OnboardingCancellationDetachesWhileEqualIntentJoinsAndDifferentIntentIsBounded()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        using var canceled = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken actual = default;
        fixture.Store.SaveHandler = async (command, token) =>
        { actual = token; entered.SetResult(); await release.Task; return fixture.Store.Commit(command); };
        var detached = fixture.ViewModel.CompleteOnboardingAsync(1, canceled.Token);
        Task<UiPreferencesSnapshot> joined;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => detached);
            Assert.False(actual.IsCancellationRequested);
            joined = fixture.ViewModel.CompleteOnboardingAsync(1);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ViewModel.CompleteOnboardingAsync(2));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(1, (await joined.WaitAsync(TimeSpan.FromSeconds(5))).Values.OnboardingVersion);
        Assert.Single(fixture.Store.Commands);
    }

    [Fact]
    public async Task DisposalCancelsQueuedOnboardingAndStillDrainsTheActualHintWrite()
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
            var onboarding = fixture.ViewModel.CompleteOnboardingAsync(1);
            disposing = fixture.ViewModel.DisposeAsync().AsTask();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => onboarding);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hint);
            Assert.False(disposing.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(fixture.Store.Commands);
        Assert.Equal(0, fixture.Store.Current.Values.OnboardingVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADefaultOrOlderProfileResultNeverClaimsTheSavedRevisionApplied(bool returnsNull)
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.EditAsync();
        fixture.Profile.Handler = (_, _, _) => Task.FromResult(returnsNull ? null! : SettingsProfile.Snapshot(WatchMonitorProfile.Normal, 0));
        await fixture.ViewModel.SaveCommand.ExecuteAsync();
        Assert.Equal(1, fixture.ViewModel.Model.State.Stored!.Revision);
        Assert.True(fixture.ViewModel.Model.State.IsApplyPending);
        Assert.False(fixture.ViewModel.Model.State.SaveUncertain);
    }

    [Fact]
    public async Task AnUncertainSaveCannotBeDiscardedOrEditedBeforeResolution()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.EditAsync();
        fixture.Store.SaveHandler = (_, _) => throw new IOException("uncertain save");
        await fixture.ViewModel.SaveCommand.ExecuteAsync();
        await fixture.Dispatcher.InvokeAsync(() => Assert.Throws<InvalidOperationException>(() => fixture.ViewModel.Language = UiLanguage.English));
        Assert.False(fixture.ViewModel.DiscardChangesCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.Model.State.CanEdit);
    }

    [Fact]
    public async Task InvalidPublicResultAndOnboardingInputsFailBeforeAnyWrite()
    {
        await using var fixture = new SettingsFixture();
        Assert.Throws<ArgumentOutOfRangeException>(() => new SettingsHintResult((SettingsHintStatus)99, fixture.Store.Current));
        Assert.Throws<ArgumentNullException>(() => new SettingsHintResult(SettingsHintStatus.Saved, null!));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.ViewModel.CompleteOnboardingAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.ViewModel.CompleteOnboardingAsync(10001));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ViewModel.CompleteOnboardingAsync(1, new CancellationToken(true)));
        Assert.Empty(fixture.Store.Commands);
    }
}
