using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Windows.Presentation.Common;

namespace SnuWatcher.Windows.Presentation.Shell;

public sealed partial class StudentShellViewModel
{
    private async Task LoadCoreAsync(CancellationToken token)
    {
        try
        {
            await Settings.LoadAsync(token).ConfigureAwait(false);
            var preferences = Settings.Model.State.Stored ?? throw new InvalidOperationException();
            var hints = await termHints.ReadAsync(token).ConfigureAwait(false);
            var source = await dashboard.ReadSourceStatusAsync(token).ConfigureAwait(false);
            if (hints is null || source is null) throw new InvalidOperationException();
            await dispatcher.InvokeAsync(() =>
            {
                if (closed) return;
                if (!State.IsLoaded && preferences.Values.SelectedYear is { } year) Search.SelectedYear = year;
                storedSource = source; localSavedStateError = false;
                Model.Replace(State with { IsLoaded = true, TermHints = hints, IssueKey = null });
                PublishSource();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { await SavedStateFailedAsync().ConfigureAwait(false); }
    }

    private async Task RefreshSourceCoreAsync(CancellationToken token)
    {
        try
        {
            var source = await dashboard.ReadSourceStatusAsync(token).ConfigureAwait(false);
            if (source is null) throw new InvalidOperationException();
            await dispatcher.InvokeAsync(() =>
            {
                if (closed) return;
                storedSource = source; localSavedStateError = false;
                Model.Replace(State with { IssueKey = null }); PublishSource();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { await SavedStateFailedAsync().ConfigureAwait(false); }
    }

    private async Task ResumeAccessCoreAsync(CancellationToken token)
    {
        try
        {
            long revision = 0;
            await dispatcher.InvokeAsync(() => revision = storedSource?.Policy.Revision ?? throw new InvalidOperationException(), token).ConfigureAwait(false);
            await controls!.ResumeSourceAccessAsync(revision, token).ConfigureAwait(false);
            await RefreshSourceCoreAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { await SavedStateFailedAsync().ConfigureAwait(false); }
    }

    private async Task RecoverPolicyCoreAsync(CancellationToken token)
    {
        try
        {
            await controls!.RecoverSourcePolicyAsync(token).ConfigureAwait(false);
            await RefreshSourceCoreAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { await SavedStateFailedAsync().ConfigureAwait(false); }
    }

    private async Task CompleteOnboardingCoreAsync(CancellationToken token)
    {
        try
        {
            if (!Settings.Model.State.IsLoaded) await Settings.LoadAsync(token).ConfigureAwait(false);
            if (Settings.Model.State.IsDirty)
            {
                var saved = await Settings.SaveCommand.ExecuteAsync(token).ConfigureAwait(false);
                if (saved != UiCommandOutcome.Completed) { await OnboardingPendingAsync().ConfigureAwait(false); return; }
            }
            var state = Settings.Model.State;
            if (state.Stored is null || state.IsDirty || state.SaveUncertain || state.HasConflict)
            { await OnboardingPendingAsync().ConfigureAwait(false); return; }
            token.ThrowIfCancellationRequested();
            var receipt = await Settings.CompleteOnboardingAsync(1, token).ConfigureAwait(false);
            await dispatcher.InvokeAsync(() => { if (!closed) { PreferencesAccepted(receipt); Model.Replace(State with { IssueKey = null }); } }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { await OnboardingPendingAsync().ConfigureAwait(false); }
    }

    private Task OnboardingPendingAsync() => dispatcher.InvokeAsync(() =>
    { if (!closed) Model.Replace(State with { IssueKey = "Ui.SettingsPending" }); });

    private Task SavedStateFailedAsync() => dispatcher.InvokeAsync(() =>
    {
        if (closed) return;
        localSavedStateError = true;
        Model.Replace(State with { IssueKey = "Ui.SavedStateError" }); PublishSource();
        Report("ui_shell_saved_state_failed");
    });
}
