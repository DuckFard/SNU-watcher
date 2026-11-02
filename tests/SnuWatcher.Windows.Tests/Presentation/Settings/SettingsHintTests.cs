using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Windows.Presentation.Settings;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Settings;

public sealed class SettingsHintTests
{
    [Fact]
    public async Task HintSaveChangesOnlyHintFieldsAndNeverSavesUnsavedEditorValues()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.EditAsync();
        var result = await fixture.ViewModel.PersistTermHintAsync(2026, "21");
        Assert.Equal(SettingsHintStatus.Saved, result.Status);
        var saved = Assert.Single(fixture.Store.Commands).Values;
        Assert.Equal(UiLanguage.English, saved.Language);
        Assert.Equal(WatchMonitorProfile.Normal, saved.DesiredProfile);
        Assert.Equal(UiCloseBehavior.MinimizeToTray, saved.CloseBehavior);
        Assert.Equal(2026, saved.SelectedYear);
        Assert.Equal("21", saved.TermHint!.Selector);
        Assert.Equal(UiLanguage.Korean, fixture.ViewModel.Language);
        Assert.Equal(WatchMonitorProfile.Faster, fixture.ViewModel.DesiredProfile);
        Assert.True(fixture.ViewModel.Model.State.IsDirty);
        Assert.Equal(WatchMonitorProfile.Normal, Assert.Single(fixture.Profile.Calls).Profile);
        Assert.Equal(result.CurrentSnapshot.Revision, fixture.ViewModel.Model.State.Applied!.PreferencesRevision);
    }

    [Fact]
    public async Task NewHintsCoalesceToOneLatestPendingIntentBehindTheActualSave()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        fixture.Store.SaveHandler = async (command, token) =>
        {
            if (Interlocked.Increment(ref count) == 1) { entered.SetResult(); await release.Task.WaitAsync(token); }
            return fixture.Store.Commit(command);
        };
        var first = fixture.ViewModel.PersistTermHintAsync(2026, "21");
        Task<SettingsHintResult> second;
        Task<SettingsHintResult> latest;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = fixture.ViewModel.PersistTermHintAsync(2027, "reported");
            latest = fixture.ViewModel.PersistTermHintAsync(2028, null);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(2026, (await first).CurrentSnapshot.Values.SelectedYear);
        var savedLatest = await latest.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(savedLatest, await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2028, savedLatest.CurrentSnapshot.Values.SelectedYear);
        Assert.Null(savedLatest.CurrentSnapshot.Values.TermHint);
        Assert.Equal(new[] { 2026, 2028 }, fixture.Store.Commands.Select(command => command.Values.SelectedYear!.Value));
    }

    [Fact]
    public async Task CancelingAHintWaiterDetachesWithoutCancelingItsAcceptedSave()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        using var canceled = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken actualToken = default;
        fixture.Store.SaveHandler = async (command, token) =>
        { actualToken = token; entered.SetResult(); await release.Task; return fixture.Store.Commit(command); };
        var detached = fixture.ViewModel.PersistTermHintAsync(2026, "21", canceled.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => detached);
            Assert.False(actualToken.IsCancellationRequested);
        }
        finally { release.TrySetResult(); }
        var joined = await fixture.ViewModel.PersistTermHintAsync(2026, "21");
        Assert.Equal(SettingsHintStatus.Saved, joined.Status);
        Assert.Single(fixture.Store.Commands);
    }

    [Fact]
    public async Task HintConflictIsExplicitAndPreservesCurrentStoredNonHintValues()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        fixture.Store.SaveHandler = (command, _) =>
        {
            fixture.Store.ExternalSave(new(UiLanguage.Korean, null, null, WatchMonitorProfile.LowActivity, UiCloseBehavior.Quit, 1));
            return Task.FromResult(fixture.Store.Commit(command));
        };
        var conflict = await fixture.ViewModel.PersistTermHintAsync(2026, "21");
        Assert.Equal(SettingsHintStatus.Conflict, conflict.Status);
        Assert.True(fixture.ViewModel.Model.State.HasConflict);
        Assert.Empty(fixture.Profile.Calls);
        fixture.Store.SaveHandler = null;
        var saved = await fixture.ViewModel.PersistTermHintAsync(2026, "21");
        Assert.Equal(SettingsHintStatus.Saved, saved.Status);
        Assert.Equal(UiLanguage.Korean, saved.CurrentSnapshot.Values.Language);
        Assert.Equal(WatchMonitorProfile.LowActivity, saved.CurrentSnapshot.Values.DesiredProfile);
        Assert.Equal(1, saved.CurrentSnapshot.Values.OnboardingVersion);
    }

    [Fact]
    public async Task HintCannotResolveOrReplaceAnUncertainUserSaveImplicitly()
    {
        await using var fixture = new SettingsFixture();
        await fixture.ViewModel.LoadAsync();
        await fixture.EditAsync();
        fixture.Store.SaveHandler = (_, _) => throw new IOException("uncertain settings write");
        await fixture.ViewModel.SaveCommand.ExecuteAsync();
        fixture.Store.SaveHandler = null;
        var pending = await fixture.ViewModel.PersistTermHintAsync(2026, "21");
        Assert.Equal(SettingsHintStatus.Pending, pending.Status);
        Assert.Single(fixture.Store.Commands);
        Assert.Empty(fixture.Profile.Calls);
        await fixture.ViewModel.SaveCommand.ExecuteAsync();
        Assert.Same(fixture.Store.Commands[0], fixture.Store.Commands[1]);
    }

    [Fact]
    public async Task InvalidOrPreCanceledHintsDoNotEnterTheOwnedRunner()
    {
        await using var fixture = new SettingsFixture();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.ViewModel.PersistTermHintAsync(2012, "21"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.ViewModel.PersistTermHintAsync(2026, "21\n"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ViewModel.PersistTermHintAsync(2026, "21", new CancellationToken(true)));
        Assert.Empty(fixture.Store.Commands);
    }
}
