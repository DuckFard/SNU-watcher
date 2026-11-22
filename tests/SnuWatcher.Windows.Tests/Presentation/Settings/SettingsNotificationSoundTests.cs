using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Windows.Presentation.Common;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Settings;

public sealed class SettingsNotificationSoundTests
{
    [Fact]
    public async Task MuteIsAnUnsavedEditUntilAKnownSavedSnapshotIsPublished()
    {
        await using var fixture = new SettingsFixture();
        var known = new List<UiPreferencesSnapshot>();
        var changed = new List<string?>();
        fixture.ViewModel.PreferencesChanged += known.Add;
        fixture.ViewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        await fixture.ViewModel.LoadAsync(); known.Clear(); changed.Clear();
        Assert.False(fixture.ViewModel.NotificationSoundMuted);
        await fixture.Dispatcher.InvokeAsync(() => fixture.ViewModel.NotificationSoundMuted = true);
        Assert.True(fixture.ViewModel.Model.State.IsDirty);
        Assert.Empty(known); Assert.Empty(fixture.Store.Commands); Assert.Empty(fixture.Profile.Calls);
        Assert.Contains(nameof(fixture.ViewModel.NotificationSoundMuted), changed);
        Assert.Equal(UiCommandOutcome.Completed, await fixture.ViewModel.SaveCommand.ExecuteAsync());
        Assert.True(Assert.Single(fixture.Store.Commands).Values.NotificationSoundMuted);
        Assert.True(Assert.Single(known).Values.NotificationSoundMuted);
        Assert.False(fixture.ViewModel.Model.State.IsDirty);
    }

    [Fact]
    public async Task OtherEditorChangesKeepTheKnownMuteChoice()
    {
        await using var fixture = new SettingsFixture();
        fixture.Store.ExternalSave(Values(true));
        await fixture.ViewModel.LoadAsync();
        Assert.True(fixture.ViewModel.NotificationSoundMuted);
        await fixture.EditAsync();
        await fixture.ViewModel.SaveCommand.ExecuteAsync();
        Assert.True(Assert.Single(fixture.Store.Commands).Values.NotificationSoundMuted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HintOnlyWritesKeepStoredMuteAndPreserveTheOppositeUnsavedChoice(bool storedMute)
    {
        await using var fixture = new SettingsFixture();
        fixture.Store.ExternalSave(Values(storedMute));
        await fixture.ViewModel.LoadAsync();
        await fixture.EditAsync();
        await fixture.Dispatcher.InvokeAsync(() => fixture.ViewModel.NotificationSoundMuted = !storedMute);
        var result = await fixture.ViewModel.PersistTermHintAsync(2027, "reported");
        Assert.Equal(storedMute, result.CurrentSnapshot.Values.NotificationSoundMuted);
        Assert.Equal(storedMute, Assert.Single(fixture.Store.Commands).Values.NotificationSoundMuted);
        Assert.Equal(!storedMute, fixture.ViewModel.NotificationSoundMuted);
        Assert.Equal(UiLanguage.Korean, fixture.ViewModel.Language);
        Assert.Equal(WatchMonitorProfile.Faster, fixture.ViewModel.DesiredProfile);
        Assert.True(fixture.ViewModel.Model.State.IsDirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnboardingOnlyWritesKeepStoredMuteAndAllUnsavedEditorChoices(bool storedMute)
    {
        await using var fixture = new SettingsFixture();
        fixture.Store.ExternalSave(Values(storedMute));
        await fixture.ViewModel.LoadAsync();
        await fixture.EditAsync();
        await fixture.Dispatcher.InvokeAsync(() => fixture.ViewModel.NotificationSoundMuted = !storedMute);
        var result = await fixture.ViewModel.CompleteOnboardingAsync(1);
        Assert.Equal(1, result.Values.OnboardingVersion);
        Assert.Equal(storedMute, result.Values.NotificationSoundMuted);
        Assert.Equal(storedMute, Assert.Single(fixture.Store.Commands).Values.NotificationSoundMuted);
        Assert.Equal(!storedMute, fixture.ViewModel.NotificationSoundMuted);
        Assert.Equal(UiLanguage.Korean, fixture.ViewModel.Language);
        Assert.True(fixture.ViewModel.Model.State.IsDirty);
    }

    [Fact]
    public async Task AmbiguousMuteSaveRetriesTheOriginalCommandAndPublishesTheNewerCurrentSnapshot()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        var known = new List<UiPreferencesSnapshot>(); fixture.ViewModel.PreferencesChanged += known.Add;
        await fixture.Dispatcher.InvokeAsync(() => fixture.ViewModel.NotificationSoundMuted = true);
        fixture.Store.SaveHandler = (command, _) =>
        {
            fixture.Store.Commit(command);
            fixture.Store.ExternalSave(Values(false, WatchMonitorProfile.LowActivity));
            throw new IOException("lost accepted return");
        };
        Assert.Equal(UiCommandOutcome.Failed, await fixture.ViewModel.SaveCommand.ExecuteAsync());
        Assert.Empty(known);
        fixture.Store.SaveHandler = null;
        Assert.Equal(UiCommandOutcome.Completed, await fixture.ViewModel.SaveCommand.ExecuteAsync());
        Assert.Same(fixture.Store.Commands[0], fixture.Store.Commands[1]);
        Assert.True(fixture.Store.Commands[0].Values.NotificationSoundMuted);
        Assert.False(Assert.Single(known).Values.NotificationSoundMuted);
        Assert.False(fixture.ViewModel.NotificationSoundMuted);
        Assert.Equal(2, Assert.Single(fixture.Profile.Calls).Revision);
    }

    [Fact]
    public async Task ConflictAndReloadPreserveDraftMuteUntilExplicitDiscard()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.Dispatcher.InvokeAsync(() => fixture.ViewModel.NotificationSoundMuted = true);
        fixture.Store.ExternalSave(Values(false, WatchMonitorProfile.LowActivity));
        await fixture.ViewModel.SaveCommand.ExecuteAsync();
        Assert.True(fixture.ViewModel.Model.State.HasConflict);
        await fixture.ViewModel.LoadAsync();
        Assert.True(fixture.ViewModel.NotificationSoundMuted);
        Assert.True(fixture.ViewModel.Model.State.IsDirty);
        await fixture.ViewModel.DiscardChangesCommand.ExecuteAsync();
        Assert.False(fixture.ViewModel.NotificationSoundMuted);
        Assert.False(fixture.ViewModel.Model.State.IsDirty);
    }

    private static UiPreferencesValues Values(bool mute, WatchMonitorProfile profile = WatchMonitorProfile.Normal) =>
        new(UiLanguage.English, null, null, profile, UiCloseBehavior.MinimizeToTray, 0, mute);
}
