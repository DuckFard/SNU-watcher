using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Presentation.Preferences;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Settings;

public sealed class SettingsOnboardingTests
{
    [Fact]
    public async Task CompletionSavesOnlyOnboardingOverCurrentStoredValuesAndKeepsUnsavedEdits()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.EditAsync();
        var completed = await fixture.ViewModel.CompleteOnboardingAsync(1);
        var values = Assert.Single(fixture.Store.Commands).Values;
        Assert.Equal(1, completed.Values.OnboardingVersion);
        Assert.Equal(UiLanguage.English, values.Language);
        Assert.Equal(WatchMonitorProfile.Normal, values.DesiredProfile);
        Assert.Equal(UiLanguage.Korean, fixture.ViewModel.Language);
        Assert.True(fixture.ViewModel.Model.State.IsDirty);
        Assert.Equal(1, fixture.ViewModel.Model.State.Applied!.PreferencesRevision);
    }

    [Fact]
    public async Task UnknownOnboardingSaveRetriesItsStableCommand()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        fixture.Store.SaveHandler = (command, _) => { fixture.Store.Commit(command); throw new IOException("lost onboarding receipt"); };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ViewModel.CompleteOnboardingAsync(1));
        Assert.Equal("settings_onboarding_pending", error.Message);
        fixture.Store.SaveHandler = null;
        var saved = await fixture.ViewModel.CompleteOnboardingAsync(1);
        Assert.Equal(1, saved.Values.OnboardingVersion);
        Assert.Same(fixture.Store.Commands[0], fixture.Store.Commands[1]);
        Assert.Single(fixture.Profile.Calls);
    }

    [Fact]
    public async Task SavedOnboardingCanCompleteWithHonestPendingProfileApplication()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        fixture.Profile.Handler = (_, _, _) => throw new IOException("actor unavailable");
        var saved = await fixture.ViewModel.CompleteOnboardingAsync(1);
        Assert.Equal(1, saved.Values.OnboardingVersion);
        Assert.True(fixture.ViewModel.Model.State.IsApplyPending);
        Assert.False(fixture.ViewModel.Model.State.SaveUncertain);
    }

    [Fact]
    public async Task OnboardingCannotReplaceAnUncertainEditorSave()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.EditAsync();
        fixture.Store.SaveHandler = (_, _) => throw new IOException("uncertain edit save");
        await fixture.ViewModel.SaveCommand.ExecuteAsync();
        fixture.Store.SaveHandler = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ViewModel.CompleteOnboardingAsync(1));
        Assert.Single(fixture.Store.Commands);
        Assert.Equal(0, fixture.Store.Current.Values.OnboardingVersion);
    }
}
