using SnuWatcher.Application.Presentation.Preferences;

namespace SnuWatcher.Windows.Presentation.Settings;

public sealed partial class SettingsViewModel
{
    private enum WriteOrigin { Editor, Hint, Onboarding }
    private sealed record PendingWrite(SaveUiPreferencesCommand Command, WriteOrigin Origin, long EditVersion);
    private PendingWrite? pendingWrite;

    private async Task ReloadCoreAsync(CancellationToken token)
    {
        var stored = await preferences.ReadAsync(token).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(stored);
        var applied = refreshProfile.AppliedRefreshProfile;
        await UpdateAsync(state => state with
        {
            Stored = stored, Applied = applied,
            Editor = state.IsDirty ? state.Editor : SettingsEditor.From(stored.Values),
            IssueKey = state.SaveUncertain ? "Ui.SettingsSaveUncertain" : null,
        }, () => PublishPreferences(stored)).ConfigureAwait(false);
    }

    private async Task SaveCoreAsync(CancellationToken token)
    {
        if (pendingWrite is null)
            await dispatcher.InvokeAsync(() =>
            {
                var state = Model.State;
                var stored = state.Stored ?? throw new InvalidOperationException("Load settings before saving.");
                var editor = state.Editor;
                var values = new UiPreferencesValues(editor.Language, stored.Values.SelectedYear, stored.Values.TermHint,
                    editor.DesiredProfile, editor.CloseBehavior, stored.Values.OnboardingVersion, editor.NotificationSoundMuted);
                pendingWrite = new(new(Guid.NewGuid(), stored.Revision, values), WriteOrigin.Editor, editVersion);
            }).ConfigureAwait(false);
        var result = await WritePendingAsync(token).ConfigureAwait(false);
        if (result is not null && result.Status != UiPreferencesSaveStatus.VersionConflict)
            await ApplyCoreAsync(result.CurrentSnapshot, token).ConfigureAwait(false);
    }

    private async Task<UiPreferencesSaveResult?> WritePendingAsync(CancellationToken token)
    {
        var attempt = pendingWrite ?? throw new InvalidOperationException("A stable preference command is required.");
        try
        {
            var result = await preferences.TrySaveAsync(attempt.Command, token).ConfigureAwait(false);
            ValidateWriteResult(attempt.Command, result);
            var conflict = result.Status == UiPreferencesSaveStatus.VersionConflict;
            await UpdateAsync(state => state with
            {
                Stored = result.CurrentSnapshot, SaveUncertain = false, HasConflict = conflict,
                Editor = conflict || (attempt.Origin == WriteOrigin.Editor ? editVersion != attempt.EditVersion : state.IsDirty)
                    ? state.Editor : SettingsEditor.From(result.CurrentSnapshot.Values),
                IssueKey = conflict ? "Ui.SettingsConflict" : "Ui.SettingsPending",
            }, () => PublishPreferences(result.CurrentSnapshot)).ConfigureAwait(false);
            pendingWrite = null;
            return result;
        }
        catch (UiPreferencesReplayConflictException)
        {
            pendingWrite = null;
            await UpdateAsync(state => state with { HasConflict = true, SaveUncertain = false, IssueKey = "Ui.SettingsConflict" }).ConfigureAwait(false);
            return null;
        }
        catch (Exception)
        {
            await UpdateAsync(state => state with { SaveUncertain = true, IssueKey = "Ui.SettingsSaveUncertain" }).ConfigureAwait(false);
            throw;
        }
    }

    private static void ValidateWriteResult(SaveUiPreferencesCommand command, UiPreferencesSaveResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Receipt is { } receipt && (receipt.CommandId != command.CommandId
            || receipt.PreviousRevision != command.ExpectedRevision || receipt.SavedSnapshot.Values != command.Values))
            throw new InvalidOperationException("settings_save_result_invalid");
    }

    private async Task RetryApplyCoreAsync(CancellationToken token)
    {
        if (Model.State.Stored is { } stored) await ApplyCoreAsync(stored, token).ConfigureAwait(false);
    }

    private async Task<bool> ApplyCoreAsync(UiPreferencesSnapshot stored, CancellationToken token)
    {
        try
        {
            var applied = await refreshProfile.SetRefreshProfileAsync(stored.Values.DesiredProfile, stored.Revision, token).ConfigureAwait(false);
            if (applied is null || applied.PreferencesRevision < stored.Revision)
                throw new InvalidOperationException("settings_profile_result_invalid");
            await UpdateAsync(state =>
            {
                var next = state with { Applied = applied, IssueKey = null };
                return next with { IssueKey = next.IsApplyPending ? "Ui.SettingsPending" : "Ui.SettingsSaved" };
            }).ConfigureAwait(false);
            return !Model.State.IsApplyPending;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { await MarkApplyPendingAsync().ConfigureAwait(false); throw; }
        catch (Exception)
        { await MarkApplyPendingAsync().ConfigureAwait(false); return false; }
    }

    private Task MarkApplyPendingAsync() => UpdateAsync(state =>
    {
        var actual = state.Applied;
        try { actual = refreshProfile.AppliedRefreshProfile; }
        catch (Exception) { /* Keep the previous actual snapshot visibly historical. */ }
        return state with { Applied = actual, IssueKey = "Ui.SettingsPending" };
    });

    private Task DiscardCoreAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return UpdateAsync(state =>
        {
            if (state.Stored is null || state.SaveUncertain) return state;
            editVersion = checked(editVersion + 1);
            return state with { Editor = SettingsEditor.From(state.Stored.Values), HasConflict = false, IssueKey = null };
        });
    }
}
