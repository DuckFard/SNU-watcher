using System.ComponentModel;
using SnuWatcher.Application.Presentation.Diagnostics;
using SnuWatcher.Windows.Presentation.Common;

namespace SnuWatcher.Windows.Presentation.Settings;

/// <summary>Historical local diagnostics and a fixed user-facing outcome. No raw error is retained.</summary>
public sealed record SettingsDiagnosticsState(DiagnosticsSnapshot? Snapshot, bool IsBusy, string? IssueKey);

public sealed partial class SettingsViewModel
{
    private readonly IDiagnosticsReader? diagnostics;
    private readonly IDiagnosticsExportAction? diagnosticsExport;
    private readonly SemaphoreSlim diagnosticsLane = new(1, 1);
    /// <summary>Independent immutable diagnostics panel; refreshing it never changes preference drafts.</summary>
    public UiSnapshotModel<SettingsDiagnosticsState> DiagnosticsModel { get; }
    /// <summary>Explicit metadata/cached-status refresh; no source or native probe.</summary>
    public UiAsyncCommand RefreshDiagnosticsCommand { get; }
    /// <summary>Refreshes local diagnostics then owns the user-selected export through its actual completion.</summary>
    public UiAsyncCommand ExportDiagnosticsCommand { get; }

    private async Task RunDiagnosticsAsync(bool export, CancellationToken token)
    {
        await diagnosticsLane.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested(); ThrowIfClosed();
            await DiagnosticsUpdateAsync(state => state with { IsBusy = true, IssueKey = null }).ConfigureAwait(false);
            ThrowIfClosed(); token.ThrowIfCancellationRequested();
            var snapshot = await diagnostics!.ReadAsync(token).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(snapshot);
            await DiagnosticsUpdateAsync(state => state with { Snapshot = snapshot, IssueKey = "Ui.DiagnosticsChecked" }).ConfigureAwait(false);
            if (!export) return;
            ThrowIfClosed(); token.ThrowIfCancellationRequested();
            var result = await diagnosticsExport!.ExportAsync(snapshot, token).ConfigureAwait(false);
            if (!Enum.IsDefined(result)) throw new InvalidOperationException("Invalid diagnostics export outcome.");
            // The action owns any open dialog and actual file write. A completed save survives late cancellation.
            await DiagnosticsUpdateAsync(state => state with { IssueKey = result == DiagnosticsExportOutcome.Saved
                ? "Ui.DiagnosticsSaved" : "Ui.DiagnosticsCanceled" }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await DiagnosticsUpdateAsync(state => state with { IssueKey = "Ui.DiagnosticsCanceled" }).ConfigureAwait(false);
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Optional local diagnostics must not turn an ordinary destination/access failure
            // into the desktop's fatal presentation-failure signal or stop course monitoring.
            await DiagnosticsUpdateAsync(state => state with { IssueKey = "Ui.DiagnosticsFailed" }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await DiagnosticsUpdateAsync(state => state with { IssueKey = "Ui.DiagnosticsFailed" }).ConfigureAwait(false);
            throw;
        }
        finally
        {
            try { await DiagnosticsUpdateAsync(state => state with { IsBusy = false }).ConfigureAwait(false); }
            finally { diagnosticsLane.Release(); }
        }
    }
    private Task DiagnosticsUpdateAsync(Func<SettingsDiagnosticsState, SettingsDiagnosticsState> update) =>
        dispatcher.InvokeAsync(() => DiagnosticsModel.Replace(update(DiagnosticsModel.State)));
    private void DiagnosticsChanged(object? sender, PropertyChangedEventArgs args)
    { RefreshDiagnosticsCommand.NotifyCanExecuteChanged(); ExportDiagnosticsCommand.NotifyCanExecuteChanged(); }
    private async Task DisposeDiagnosticsLaneAsync(Task drain)
    {
        try { await drain.ConfigureAwait(false); }
        finally { DiagnosticsModel.PropertyChanged -= DiagnosticsChanged; diagnosticsLane.Dispose(); }
    }
}
