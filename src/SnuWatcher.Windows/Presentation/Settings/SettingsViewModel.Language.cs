using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Windows.Presentation.Common;

namespace SnuWatcher.Windows.Presentation.Settings;

public sealed partial class SettingsViewModel
{
    private sealed record LanguageRequest(UiLanguage Language);
    private LanguageRequest? requestedLanguage;

    /// <summary>The header's "KR | EN" save; see <see cref="SaveLanguageAsync"/>.</summary>
    public UiAsyncCommand LanguageCommand { get; private set; } = null!;

    /// <summary>
    /// Saves only the language over the current stored values, as the header's "KR | EN" links do. Unsaved Settings
    /// edits (sound, close behavior, monitoring profile) stay unsaved; their language follows the saved choice so a later
    /// Settings save cannot undo it. An unknown write retries its own stable command; any other unresolved write must be
    /// resolved on the Settings page first. A click while another settings operation runs (such as the background
    /// term save at startup) waits for it instead of being dropped.
    /// </summary>
    public Task<UiCommandOutcome> SaveLanguageAsync(UiLanguage language, CancellationToken cancellationToken = default)
    {
        ValidateEnum(language);
        ThrowIfClosed();
        if (cancellationToken.IsCancellationRequested) return Task.FromResult(UiCommandOutcome.Canceled);
        var request = new LanguageRequest(language);
        if (Interlocked.CompareExchange(ref requestedLanguage, request, null) is not null)
            return Task.FromResult(UiCommandOutcome.Busy);
        return ExecuteLanguageRequestAsync(request, cancellationToken);
    }

    private async Task<UiCommandOutcome> ExecuteLanguageRequestAsync(LanguageRequest request, CancellationToken token)
    {
        // Keep this immutable payload until the actual command and its final publication have completed.
        // Busy, unavailable, or already-canceled callers must never replace an admitted request's language.
        try { return await LanguageCommand.ExecuteAsync(token).ConfigureAwait(false); }
        finally { Interlocked.CompareExchange(ref requestedLanguage, null, request); }
    }

    /// <summary>
    /// The header status for a language-link outcome, or null when there is nothing new to report: a completed
    /// or canceled request, or a duplicate click while an earlier language request is still running.
    /// </summary>
    public string? LanguageLinkIssueKey(UiCommandOutcome outcome) => outcome is UiCommandOutcome.Failed or UiCommandOutcome.Unavailable
        ? Model.State.SaveUncertain ? "Ui.SettingsSaveUncertain" : Model.State.HasConflict ? "Ui.SettingsConflict" : "Ui.CommandFailed"
        : null;

    // Not gated on IsBusy: the operation lane serializes the request behind any running settings operation.
    private UiAsyncCommand CreateLanguageCommand() => Command(
        token => SaveLanguageCoreAsync((Volatile.Read(ref requestedLanguage)
            ?? throw new InvalidOperationException("settings_language_request_missing")).Language, token),
        () => !Model.State.SaveUncertain || pendingWrite is { Origin: WriteOrigin.Language });

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
