using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Windows.Presentation.Common;
using SnuWatcher.Windows.Presentation.Settings;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Settings;

/// <summary>The header's "KR | EN" links save only the language.</summary>
public sealed class SettingsLanguageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARejectedSecondLanguageRequestCannotReplaceTheAdmittedLanguage(bool secondAlreadyCanceled)
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        using var release = new ManualResetEventSlim();
        var deadline = TimeSpan.FromSeconds(30);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = 0;
        fixture.ViewModel.LanguageCommand.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(UiAsyncCommand.IsExecuting)
                || !fixture.ViewModel.LanguageCommand.IsExecuting || Interlocked.Exchange(ref blocked, 1) != 0) return;
            entered.TrySetResult();
            if (!release.Wait(deadline)) throw new TimeoutException("Release the language publication fixture.");
        };
        // Hold the admitted command before its callback captures the language, without blocking the caller.
        var first = fixture.ViewModel.SaveLanguageAsync(UiLanguage.Korean);
        try
        {
            await entered.Task.WaitAsync(deadline);
            using var canceled = new CancellationTokenSource();
            if (secondAlreadyCanceled) canceled.Cancel();
            var second = await fixture.ViewModel.SaveLanguageAsync(UiLanguage.English, canceled.Token);
            Assert.Equal(secondAlreadyCanceled ? UiCommandOutcome.Canceled : UiCommandOutcome.Busy, second);
        }
        finally { release.Set(); }

        Assert.Equal(UiCommandOutcome.Completed, await first.WaitAsync(deadline));
        Assert.Equal(UiLanguage.Korean, Assert.Single(fixture.Store.Commands).Values.Language);
        Assert.Equal(UiLanguage.Korean, fixture.ViewModel.Model.State.Stored!.Values.Language);
        Assert.Empty(fixture.Errors);
    }

    [Fact]
    public async Task TheLanguageLinkSavesOnlyTheLanguageAndUnsavedEditsStayUnsavedWithTheNewLanguage()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.EditAsync(language: UiLanguage.English, profile: WatchMonitorProfile.Faster, close: UiCloseBehavior.Quit);
        await fixture.Dispatcher.InvokeAsync(() => fixture.ViewModel.NotificationSoundMuted = true);
        var published = new List<UiPreferencesSnapshot>();
        fixture.ViewModel.PreferencesChanged += published.Add;

        Assert.Equal(UiCommandOutcome.Completed, await fixture.ViewModel.SaveLanguageAsync(UiLanguage.Korean));
        var saved = Assert.Single(fixture.Store.Commands).Values;
        Assert.Equal(new UiPreferencesValues(UiLanguage.Korean, null, null, WatchMonitorProfile.Normal, UiCloseBehavior.MinimizeToTray, 0), saved);
        Assert.Equal(UiLanguage.Korean, fixture.ViewModel.Model.State.Stored!.Values.Language);
        Assert.Contains(published, snapshot => snapshot.Values.Language == UiLanguage.Korean);
        Assert.True(fixture.ViewModel.Model.State.IsDirty);
        Assert.Equal((UiLanguage.Korean, WatchMonitorProfile.Faster, UiCloseBehavior.Quit, true),
            (fixture.ViewModel.Language, fixture.ViewModel.DesiredProfile, fixture.ViewModel.CloseBehavior, fixture.ViewModel.NotificationSoundMuted));
        Assert.Equal(1, fixture.ViewModel.Model.State.Applied!.PreferencesRevision);

        // A later Settings save keeps the linked language rather than reverting it.
        await fixture.ViewModel.SaveCommand.ExecuteAsync();
        Assert.Equal((UiLanguage.Korean, WatchMonitorProfile.Faster, UiCloseBehavior.Quit, true),
            (fixture.Store.Current.Values.Language, fixture.Store.Current.Values.DesiredProfile,
             fixture.Store.Current.Values.CloseBehavior, fixture.Store.Current.Values.NotificationSoundMuted));
    }

    [Fact]
    public async Task TheSavedLanguageWritesNothingAndOnlyRealignsTheEditor()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.Dispatcher.InvokeAsync(() => fixture.ViewModel.Language = UiLanguage.Korean);
        Assert.Equal(UiCommandOutcome.Completed, await fixture.ViewModel.SaveLanguageAsync(UiLanguage.English));
        Assert.Empty(fixture.Store.Commands);
        Assert.Equal(UiLanguage.English, fixture.ViewModel.Language);
        Assert.False(fixture.ViewModel.Model.State.IsDirty);
    }

    [Fact]
    public async Task AnUnknownLanguageSaveRetriesItsOwnStableCommandAndNothingElse()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        fixture.Store.SaveHandler = (command, _) => { fixture.Store.Commit(command); throw new IOException("lost language receipt"); };
        Assert.Equal(UiCommandOutcome.Failed, await fixture.ViewModel.SaveLanguageAsync(UiLanguage.Korean));
        Assert.True(fixture.ViewModel.Model.State.SaveUncertain);
        Assert.True(fixture.ViewModel.LanguageCommand.CanExecute(null));
        fixture.Store.SaveHandler = null;
        Assert.Equal(UiCommandOutcome.Failed, await fixture.ViewModel.SaveLanguageAsync(UiLanguage.English));
        Assert.Single(fixture.Store.Commands);
        Assert.Equal(UiCommandOutcome.Completed, await fixture.ViewModel.SaveLanguageAsync(UiLanguage.Korean));
        Assert.Same(fixture.Store.Commands[0], fixture.Store.Commands[1]);
        Assert.False(fixture.ViewModel.Model.State.SaveUncertain);
        Assert.Equal(UiLanguage.Korean, fixture.ViewModel.Language);
    }

    [Fact]
    public async Task AnotherUnresolvedWriteOrAConflictBlocksTheLanguageLink()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.EditAsync();
        fixture.Store.SaveHandler = (_, _) => throw new IOException("uncertain edit save");
        await fixture.ViewModel.SaveCommand.ExecuteAsync();
        Assert.False(fixture.ViewModel.LanguageCommand.CanExecute(null));
        Assert.Equal(UiCommandOutcome.Unavailable, await fixture.ViewModel.SaveLanguageAsync(UiLanguage.Korean));

        await using var conflicted = new SettingsFixture();
        await conflicted.ViewModel.LoadAsync();
        conflicted.Store.SaveHandler = (command, _) =>
        {
            conflicted.Store.ExternalSave(new UiPreferencesValues(UiLanguage.English, null, null, WatchMonitorProfile.LowActivity, UiCloseBehavior.MinimizeToTray, 0));
            return Task.FromResult(conflicted.Store.Commit(command));
        };
        Assert.Equal(UiCommandOutcome.Failed, await conflicted.ViewModel.SaveLanguageAsync(UiLanguage.Korean));
        Assert.True(conflicted.ViewModel.Model.State.HasConflict);
        Assert.Equal(UiLanguage.English, conflicted.Store.Current.Values.Language);
    }

    [Fact]
    public async Task ALanguageClickDuringTheBackgroundTermSaveWaitsForItAndThenSaves()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        var deadline = TimeSpan.FromSeconds(30);
        var applying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // A slow monitor holds the startup term save in its profile application, keeping Settings busy.
        fixture.Profile.Handler = async (profile, revision, token) =>
        {
            applying.TrySetResult();
            await release.Task.WaitAsync(token);
            return SettingsProfile.Snapshot(profile, revision);
        };
        var hint = fixture.ViewModel.PersistTermHintAsync(2026, "21");
        Task<UiCommandOutcome> language;
        try
        {
            await applying.Task.WaitAsync(deadline);
            Assert.True(fixture.ViewModel.Model.State.IsBusy);
            Assert.True(fixture.ViewModel.LanguageCommand.CanExecute(null));
            language = fixture.ViewModel.SaveLanguageAsync(UiLanguage.Korean);
            Assert.False(language.IsCompleted);
        }
        finally { fixture.Profile.Handler = null; release.TrySetResult(); }

        Assert.Equal(UiCommandOutcome.Completed, await language.WaitAsync(deadline));
        Assert.Equal(SettingsHintStatus.Saved, (await hint.WaitAsync(deadline)).Status);
        // The click was kept, and its language save kept the term the background save had just written.
        Assert.Equal((UiLanguage.Korean, 2026), (fixture.Store.Current.Values.Language, fixture.Store.Current.Values.SelectedYear));
        Assert.Equal(2, fixture.Store.Commands.Length);
        Assert.Equal(UiLanguage.Korean, fixture.ViewModel.Language);
        Assert.Null(fixture.ViewModel.LanguageLinkIssueKey(UiCommandOutcome.Completed));
        Assert.Empty(fixture.Errors);
    }

    [Fact]
    public async Task TheHeaderReportsTheActualCauseOfAnUnsuccessfulLanguageClick()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        foreach (var quiet in new[] { UiCommandOutcome.Completed, UiCommandOutcome.Canceled, UiCommandOutcome.Busy })
            Assert.Null(fixture.ViewModel.LanguageLinkIssueKey(quiet));
        Assert.Equal("Ui.CommandFailed", fixture.ViewModel.LanguageLinkIssueKey(UiCommandOutcome.Failed));

        await fixture.EditAsync();
        fixture.Store.SaveHandler = (_, _) => throw new IOException("uncertain edit save");
        await fixture.ViewModel.SaveCommand.ExecuteAsync();
        var blocked = await fixture.ViewModel.SaveLanguageAsync(UiLanguage.Korean);
        Assert.Equal(UiCommandOutcome.Unavailable, blocked);
        Assert.Equal("Ui.SettingsSaveUncertain", fixture.ViewModel.LanguageLinkIssueKey(blocked));

        await using var conflicted = new SettingsFixture();
        await conflicted.ViewModel.LoadAsync();
        conflicted.Store.SaveHandler = (command, _) =>
        {
            conflicted.Store.ExternalSave(new UiPreferencesValues(UiLanguage.English, null, null, WatchMonitorProfile.LowActivity, UiCloseBehavior.MinimizeToTray, 0));
            return Task.FromResult(conflicted.Store.Commit(command));
        };
        var failed = await conflicted.ViewModel.SaveLanguageAsync(UiLanguage.Korean);
        Assert.Equal(UiCommandOutcome.Failed, failed);
        Assert.Equal("Ui.SettingsConflict", conflicted.ViewModel.LanguageLinkIssueKey(failed));
    }

    [Fact]
    public async Task AnUndefinedLanguageOrAClosedModelIsRejected()
    {
        var fixture = new SettingsFixture();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.ViewModel.SaveLanguageAsync((UiLanguage)99));
        await fixture.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.ViewModel.SaveLanguageAsync(UiLanguage.Korean));
    }
}
