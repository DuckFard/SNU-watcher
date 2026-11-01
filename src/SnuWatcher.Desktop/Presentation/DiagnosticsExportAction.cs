using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SnuWatcher.Application.Presentation.Diagnostics;
using SnuWatcher.Windows.Diagnostics;
using SnuWatcher.Windows.Presentation.Common;

namespace SnuWatcher.Desktop.Presentation;

/// <summary>Explicit destination choice. The modal dialog remains owned until dismissed; cancellation never abandons its UI task.</summary>
internal sealed class DiagnosticsExportAction(IUiDispatcher dispatcher) : IDiagnosticsExportAction
{
    public async Task<DiagnosticsExportOutcome> ExportAsync(DiagnosticsSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string? selected = null;
        await dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dialog = new SaveFileDialog
            {
                Title = System.Windows.Application.Current.TryFindResource("Ui.DiagnosticsExport") as string ?? "Export diagnostics",
                Filter = "JSON (*.json)|*.json", DefaultExt = ".json", AddExtension = true,
                FileName = "snu-watcher-diagnostics.json", OverwritePrompt = false, CheckPathExists = true,
            };
            if (dialog.ShowDialog() == true) selected = dialog.FileName;
        }, cancellationToken).ConfigureAwait(false);
        // Windows' modal Save dialog cannot be canceled by a token. Wait for dismissal, then decide honestly.
        if (selected is null || cancellationToken.IsCancellationRequested) return DiagnosticsExportOutcome.Canceled;
        return await DiagnosticsFileExporter.SaveAsync(selected, snapshot, cancellationToken).ConfigureAwait(false);
    }
}
