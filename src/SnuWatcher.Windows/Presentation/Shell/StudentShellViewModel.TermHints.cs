using System.ComponentModel;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Windows.Presentation.Search;
using SnuWatcher.Windows.Presentation.Settings;

namespace SnuWatcher.Windows.Presentation.Shell;

public sealed partial class StudentShellViewModel
{
    private AcademicTermHint? pendingTermHint, lastRequestedTermHint;
    private bool termHintRunning;
    private Task termHintDriver = Task.CompletedTask;

    private void SearchSelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        RequireUi();
        if (closed) return;
        var state = Search.Model.State;
        if (state.CatalogState != CourseUiLoadState.Complete || state.SelectedYear is not { } year
            || state.SelectedTerm is not { } selected || selected.Year != year
            || !state.Terms.Any(term => term == selected && term.Year == year)) return;
        var pair = new AcademicTermHint(year, selected.Selector);
        if (pair == lastRequestedTermHint) return;
        lastRequestedTermHint = pair;
        pendingTermHint = pair;
        if (termHintRunning) return;
        termHintRunning = true;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        termHintDriver = PersistTermHintsAsync(ready.Task, termHintDriver);
        ready.SetResult();
    }

    private async Task PersistTermHintsAsync(Task ready, Task previous)
    {
        await ready.ConfigureAwait(false);
        await previous.ConfigureAwait(false);
        try
        {
            while (true)
            {
                AcademicTermHint? pair = null;
                await dispatcher.InvokeAsync(() =>
                {
                    if (closed || pendingTermHint is null) { termHintRunning = false; return; }
                    pair = pendingTermHint; pendingTermHint = null;
                }).ConfigureAwait(false);
                if (pair is null) return;
                routeStop.Token.ThrowIfCancellationRequested();
                try
                {
                    // Once the preference helper admits a save, retain its actual known outcome.
                    // Closing the shell drops pending pairs and drains this one accepted helper.
                    var result = await Settings.PersistTermHintAsync(pair.Year, pair.Selector, CancellationToken.None).ConfigureAwait(false);
                    var saved = result.Status == SettingsHintStatus.Saved && result.CurrentSnapshot.Values.SelectedYear == pair.Year
                        && result.CurrentSnapshot.Values.TermHint == pair;
                    await dispatcher.InvokeAsync(() =>
                    {
                        if (closed) return;
                        if (!saved) Model.Replace(State with { IssueKey = "Ui.SettingsPending" });
                        else if (State.IssueKey == "Ui.SettingsPending" && !Settings.Model.State.SaveUncertain && !Settings.Model.State.HasConflict)
                            Model.Replace(State with { IssueKey = null });
                    }).ConfigureAwait(false);
                }
                catch (Exception) { await OnboardingPendingAsync().ConfigureAwait(false); }
            }
        }
        catch (OperationCanceledException) when (routeStop.IsCancellationRequested) { }
    }
}
