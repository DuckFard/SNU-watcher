using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SnuWatcher.Application.Presentation.Diagnostics;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Desktop.Presentation;
using SnuWatcher.Windows.Diagnostics;

namespace SnuWatcher.Desktop.Runtime;

internal static partial class StudentUiValidation
{
    /// <summary>
    /// Exercises the actual bound local reader and serializer inside the existing settings journey.
    /// The destination dialog is deliberately not invoked by this no-native private route.
    /// </summary>
    private static async Task VerifyLocalDiagnosticsUiAsync(StudentWindow window, StudentDesktopSession graph,
        StudentUiValidationSource source, StudentUiSimulatedNativeControls native, CancellationToken token)
    {
        _stage = "local_diagnostics";
        var saved = graph.Settings.Model.State.Stored;
        var requests = (source.CatalogCalls, source.InteractiveSearchCalls, source.DetailsCalls, native.StatusProbes);
        // Automatic watch polling is independent; only explicit interactive/probe calls are compared.
        await RequireReachableAsync(BoundButton(window, graph.Settings.RefreshDiagnosticsCommand), window,
            "ui_diagnostics_control_unreachable");
        await InvokeCommandAsync(window, graph.Settings.RefreshDiagnosticsCommand, token);
        var panel = graph.Settings.DiagnosticsModel.State;
        var snapshot = panel.Snapshot ?? throw new InvalidOperationException("ui_diagnostics_snapshot_missing");
        Require(!panel.IsBusy && panel.IssueKey == "Ui.DiagnosticsChecked",
            "ui_diagnostics_read_failed");
        Require(snapshot.Storage.Database.State == DiagnosticFileState.Available
            && snapshot.Storage.Database.Bytes > 0 && snapshot.Storage.TotalBytes > 0,
            "ui_private_storage_metadata_unavailable");
        Require(snapshot.Runtime.Preferences.DesiredProfile == WatchMonitorProfile.Faster
            && snapshot.Runtime.Preferences.Language == saved!.Values.Language
            && ReferenceEquals(saved, graph.Settings.Model.State.Stored), "ui_diagnostics_changed_preferences");
        Require(requests == (source.CatalogCalls, source.InteractiveSearchCalls, source.DetailsCalls, native.StatusProbes),
            "ui_diagnostics_requested_external_work");
        var bytes = DiagnosticsJson.Serialize(snapshot);
        Require(bytes.Length is > 0 and <= DiagnosticsJson.MaximumBytes, "ui_diagnostics_export_bound_failed");
        using var json = JsonDocument.Parse(bytes);
        var expected = new[] { "applicationVersion", "capturedAtUtc", "format", "runtime", "runtimeVersion", "storage" };
        Require(json.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(expected, StringComparer.Ordinal), "ui_diagnostics_export_schema_changed");
        Require(json.RootElement.GetProperty("format").GetString() == "snu-watcher-diagnostics-v1"
            && graph.LastErrorCode is null, "ui_diagnostics_export_or_graph_failed");
    }
}
