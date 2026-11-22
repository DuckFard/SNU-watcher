using SnuWatcher.Application.Presentation.Preferences;

namespace SnuWatcher.Windows.Presentation.Settings;

public sealed partial class SettingsViewModel
{
    private sealed record OnboardingIntent(int Version, TaskCompletionSource<UiPreferencesSnapshot> Completion);
    private OnboardingIntent? onboardingIntent;
    private Task onboardingDriver = Task.CompletedTask;

    /// <summary>
    /// Changes only the saved onboarding version. Known saves return their current snapshot even
    /// when actor application is pending; unknown or conflicting saves retain intent for explicit retry.
    /// Canceling a caller detaches it while this model retains the actual operation.
    /// </summary>
    public Task<UiPreferencesSnapshot> CompleteOnboardingAsync(int version, CancellationToken cancellationToken = default)
    {
        if (version is < 0 or > 10000) throw new ArgumentOutOfRangeException(nameof(version));
        cancellationToken.ThrowIfCancellationRequested();
        Task<UiPreferencesSnapshot> result;
        TaskCompletionSource? ready = null;
        lock (lifetimeSync)
        {
            ThrowIfClosed();
            if (onboardingIntent is { } active && active.Version != version)
                throw new InvalidOperationException("settings_onboarding_pending");
            if (onboardingIntent is null)
            {
                onboardingIntent = new(version, new(TaskCreationOptions.RunContinuationsAsynchronously));
                ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
                onboardingDriver = RunOnboardingAsync(ready.Task, onboardingDriver, onboardingIntent);
            }
            result = onboardingIntent.Completion.Task;
        }
        ready?.SetResult();
        return result.WaitAsync(cancellationToken);
    }

    private async Task RunOnboardingAsync(Task ready, Task previous, OnboardingIntent intent)
    {
        await ready.ConfigureAwait(false);
        await previous.ConfigureAwait(false);
        try
        {
            var saved = await RunSerializedAsync(token => CompleteOnboardingCoreAsync(intent.Version, token), hintStop.Token).ConfigureAwait(false);
            FinishOnboarding(intent, saved);
        }
        catch (OperationCanceledException) when (hintStop.IsCancellationRequested)
        { FinishOnboarding(intent, canceled: true); }
        catch (Exception)
        {
            FinishOnboarding(intent);
        }
    }

    private void FinishOnboarding(OnboardingIntent intent, UiPreferencesSnapshot? saved = null, bool canceled = false)
    {
        lock (lifetimeSync)
        {
            onboardingIntent = null;
            if (saved is not null) intent.Completion.TrySetResult(saved);
            else if (canceled) intent.Completion.TrySetCanceled(hintStop.Token);
            else
            {
                intent.Completion.TrySetException(new InvalidOperationException("settings_onboarding_pending"));
                _ = intent.Completion.Task.Exception;
            }
        }
    }

    private async Task<UiPreferencesSnapshot> CompleteOnboardingCoreAsync(int version, CancellationToken token)
    {
        if (pendingWrite is { } pending)
        {
            if (pending.Origin != WriteOrigin.Onboarding) throw new InvalidOperationException("settings_onboarding_pending");
            var replay = await WritePendingAsync(token).ConfigureAwait(false);
            if (replay is null || replay.Status == UiPreferencesSaveStatus.VersionConflict)
                throw new InvalidOperationException("settings_onboarding_pending");
            await ApplyCoreAsync(replay.CurrentSnapshot, token).ConfigureAwait(false);
            if (replay.CurrentSnapshot.Values.OnboardingVersion >= version) return replay.CurrentSnapshot;
        }
        var current = await preferences.ReadAsync(token).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(current);
        await UpdateAsync(state => state with
        {
            Stored = current, Applied = refreshProfile.AppliedRefreshProfile,
            Editor = state.IsDirty ? state.Editor : SettingsEditor.From(current.Values),
        }, () => PublishPreferences(current)).ConfigureAwait(false);
        if (current.Values.OnboardingVersion >= version)
        { await ApplyCoreAsync(current, token).ConfigureAwait(false); return current; }
        var values = current.Values;
        var completed = new UiPreferencesValues(values.Language, values.SelectedYear, values.TermHint,
            values.DesiredProfile, values.CloseBehavior, version, values.NotificationSoundMuted);
        pendingWrite = new(new(Guid.NewGuid(), current.Revision, completed), WriteOrigin.Onboarding, 0);
        var result = await WritePendingAsync(token).ConfigureAwait(false);
        if (result is null || result.Status == UiPreferencesSaveStatus.VersionConflict)
            throw new InvalidOperationException("settings_onboarding_pending");
        await ApplyCoreAsync(result.CurrentSnapshot, token).ConfigureAwait(false);
        return result.CurrentSnapshot;
    }
}
