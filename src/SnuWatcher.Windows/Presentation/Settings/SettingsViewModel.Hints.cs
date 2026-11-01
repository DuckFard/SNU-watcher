using SnuWatcher.Application.Presentation.Preferences;

namespace SnuWatcher.Windows.Presentation.Settings;

public sealed partial class SettingsViewModel
{
    private sealed record HintIntent(int Year, AcademicTermHint? Term, TaskCompletionSource<SettingsHintResult> Completion);
    private HintIntent? pendingHint;
    private HintIntent? claimedHint;
    private bool hintRunning;
    private Task hintDriver = Task.CompletedTask;

    /// <summary>
    /// Saves only year/term over current stored values. One latest pending hint coalesces callers;
    /// cancellation detaches a caller and never cancels an accepted shared save.
    /// </summary>
    public Task<SettingsHintResult> PersistTermHintAsync(int year, string? selector, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var term = selector is null ? null : new AcademicTermHint(year, selector);
        var defaults = UiPreferencesValues.Defaults;
        _ = new UiPreferencesValues(defaults.Language, year, term, defaults.DesiredProfile, defaults.CloseBehavior, defaults.OnboardingVersion);
        Task<SettingsHintResult> result;
        TaskCompletionSource? ready = null;
        lock (lifetimeSync)
        {
            ThrowIfClosed();
            var completion = pendingHint?.Completion ?? new(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingHint = new(year, term, completion);
            result = completion.Task;
            if (!hintRunning)
            {
                hintRunning = true;
                ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
                hintDriver = RunHintsAsync(ready.Task, hintDriver);
            }
        }
        ready?.SetResult();
        return result.WaitAsync(cancellationToken);
    }

    private async Task RunHintsAsync(Task ready, Task previous)
    {
        await ready.ConfigureAwait(false);
        await previous.ConfigureAwait(false);
        while (true)
        {
            HintIntent? intent;
            lock (lifetimeSync)
            {
                intent = pendingHint; pendingHint = null;
                if (intent is null) { hintRunning = false; return; }
                claimedHint = intent;
            }
            try
            {
                var result = await RunSerializedAsync(token => SaveHintCoreAsync(intent, token), hintStop.Token).ConfigureAwait(false);
                intent.Completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (hintStop.IsCancellationRequested)
            { intent.Completion.TrySetCanceled(hintStop.Token); }
            catch (Exception)
            {
                intent.Completion.TrySetException(new InvalidOperationException("settings_hint_failed"));
                _ = intent.Completion.Task.Exception;
            }
            finally { lock (lifetimeSync) claimedHint = null; }
        }
    }

    private async Task<SettingsHintResult> SaveHintCoreAsync(HintIntent intent, CancellationToken token)
    {
        var known = Model.State.Stored ?? new(0, UiPreferencesValues.Defaults, null);
        if (pendingWrite is { Origin: not WriteOrigin.Hint }) return new(SettingsHintStatus.Pending, known);
        try
        {
            if (pendingWrite is not null)
            {
                var replay = await WritePendingAsync(token).ConfigureAwait(false);
                if (replay is null || replay.Status == UiPreferencesSaveStatus.VersionConflict)
                    return new(SettingsHintStatus.Conflict, Model.State.Stored ?? known);
                await ApplyCoreAsync(replay.CurrentSnapshot, token).ConfigureAwait(false);
            }
            var current = await preferences.ReadAsync(token).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(current);
            await UpdateAsync(state => state with
            {
                Stored = current, Applied = refreshProfile.AppliedRefreshProfile,
                Editor = state.IsDirty ? state.Editor : SettingsEditor.From(current.Values),
            }, () => PublishPreferences(current)).ConfigureAwait(false);
            if (current.Values.SelectedYear == intent.Year && current.Values.TermHint == intent.Term)
                return new(await ApplyCoreAsync(current, token).ConfigureAwait(false) ? SettingsHintStatus.Saved : SettingsHintStatus.Pending, current);
            var values = current.Values;
            var hinted = new UiPreferencesValues(values.Language, intent.Year, intent.Term, values.DesiredProfile,
                values.CloseBehavior, values.OnboardingVersion);
            pendingWrite = new(new(Guid.NewGuid(), current.Revision, hinted), WriteOrigin.Hint, 0);
            var result = await WritePendingAsync(token).ConfigureAwait(false);
            if (result is null || result.Status == UiPreferencesSaveStatus.VersionConflict)
                return new(SettingsHintStatus.Conflict, Model.State.Stored ?? current);
            var applied = await ApplyCoreAsync(result.CurrentSnapshot, token).ConfigureAwait(false);
            return new(applied ? SettingsHintStatus.Saved : SettingsHintStatus.Pending, result.CurrentSnapshot);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            await UpdateAsync(state => state with { IssueKey = state.SaveUncertain ? "Ui.SettingsSaveUncertain" : "Ui.CommandFailed" }).ConfigureAwait(false);
            return new(SettingsHintStatus.Pending, Model.State.Stored ?? known);
        }
    }
}
