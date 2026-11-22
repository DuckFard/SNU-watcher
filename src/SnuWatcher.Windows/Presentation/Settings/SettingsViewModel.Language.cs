using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Windows.Presentation.Common;

namespace SnuWatcher.Windows.Presentation.Settings;

public sealed partial class SettingsViewModel
{
    private int requestedLanguage;

    /// <summary>The header's "KR | EN" save; see <see cref="SaveLanguageAsync"/>.</summary>
    public UiAsyncCommand LanguageCommand { get; private set; } = null!;

    /// <summary>
    /// Saves only the language over the current stored values, as the header's "KR | EN" links do. Unsaved Settings
    /// edits (sound, close behavior, monitoring profile) stay unsaved; their language follows the saved choice so a later
    /// Settings save cannot undo it. An unknown write retries its own stable command; any other unresolved write must be
    /// resolved on the Settings page first.
    /// </summary>
    public Task<UiCommandOutcome> SaveLanguageAsync(UiLanguage language, CancellationToken cancellationToken = default)
    {
        ValidateEnum(language);
        ThrowIfClosed();
        Volatile.Write(ref requestedLanguage, (int)language);
        return LanguageCommand.ExecuteAsync(cancellationToken);
    }

    private UiAsyncCommand CreateLanguageCommand() => Command(
        token => SaveLanguageCoreAsync((UiLanguage)Volatile.Read(ref requestedLanguage), token),
        () => !Model.State.IsBusy && (!Model.State.SaveUncertain || pendingWrite is { Origin: WriteOrigin.Language }));

    private async Task SaveLanguageCoreAsync(UiLanguage language, CancellationToken token)
    {
        if (pendingWrite is { } pending)
        {
            if (pending.Origin != WriteOrigin.Language || pending.Command.Values.Language != language)
                throw new InvalidOperationException("settings_language_pending");
            var replay = await WritePendingAsync(token).ConfigureAwait(false);
            if (replay is null || replay.Status == UiPreferencesSaveStatus.VersionConflict)
                throw new InvalidOperationException("settings_language_conflict");
            await FollowLanguageAsync(language).ConfigureAwait(false);
            await ApplyCoreAsync(replay.CurrentSnapshot, token).ConfigureAwait(false);
            return;
        }
        var current = await preferences.ReadAsync(token).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(current);
        await UpdateAsync(state => state with
        {
            Stored = current, Applied = refreshProfile.AppliedRefreshProfile,
            Editor = state.IsDirty ? state.Editor : SettingsEditor.From(current.Values),
        }, () => PublishPreferences(current)).ConfigureAwait(false);
        if (current.Values.Language == language) { await FollowLanguageAsync(language).ConfigureAwait(false); return; }
        var values = current.Values;
        var changed = new UiPreferencesValues(language, values.SelectedYear, values.TermHint, values.DesiredProfile,
            values.CloseBehavior, values.OnboardingVersion, values.NotificationSoundMuted);
        pendingWrite = new(new(Guid.NewGuid(), current.Revision, changed), WriteOrigin.Language, 0);
        var result = await WritePendingAsync(token).ConfigureAwait(false);
        if (result is null || result.Status == UiPreferencesSaveStatus.VersionConflict)
            throw new InvalidOperationException("settings_language_conflict");
        await FollowLanguageAsync(language).ConfigureAwait(false);
        await ApplyCoreAsync(result.CurrentSnapshot, token).ConfigureAwait(false);
    }

    private Task FollowLanguageAsync(UiLanguage language) => UpdateAsync(state =>
        state.Editor.Language == language ? state : state with { Editor = state.Editor with { Language = language } });
}
