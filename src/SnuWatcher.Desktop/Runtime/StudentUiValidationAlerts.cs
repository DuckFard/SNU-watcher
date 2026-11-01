using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Core;
using SnuWatcher.Desktop.Presentation;
using SnuWatcher.Windows.Hosting;
using SnuWatcher.Windows.Presentation.Alerts;
using SnuWatcher.Windows.Presentation.Shell;

namespace SnuWatcher.Desktop.Runtime;

internal static partial class StudentUiValidation
{
    private static async Task<StudentUiJourney> VerifySavedAlertsUiAsync(RuntimeProfile profile, StudentWindow window,
        StudentDesktopSession graph, StudentUiValidationSource source, StudentUiJourney proof,
        List<StudentUiCapture> captures, CancellationToken ct)
    {
        _stage = "saved_alert_history";
        await SaveLanguageAsync(window, graph, UiLanguage.English, ct);
        var sourceCalls = source.Calls;
        await NavigateAsync(window, graph, StudentPage.Alerts);
        Require((window.TryFindResource("Ui.AlertOriginalSeats") as string) == "Seats at opening",
            "ui_english_alert_seat_label_incorrect");
        await EventuallyAsync(() => graph.Alerts.State.IsLoaded && !graph.Alerts.State.IsLoading, ct);
        Require(!graph.Alerts.State.IsStale && graph.Alerts.State.Rows.Length is >= 1 and <= 25,
            "ui_alert_history_unavailable");
        var row = graph.Alerts.State.Rows.Where(item => item.Source.Trigger.Key == StudentUiValidationSource.Key013)
            .OrderBy(item => item.Source.Trigger.Ordinal).First();
        Require(row.Source.Trigger.CurrentSeats == 1 && row.Source.Trigger.PreviousSeats is null && row.Source.Trigger.HasInitialReason
            && row.Source.Delivery.Status == DeliveryStatus.Pending && row.Identifier == "F32.102 013",
            "ui_original_alert_facts_changed");
        var grid = Named<DataGrid>(window, "HistoryRows");
        grid.SetCurrentValue(Selector.SelectedItemProperty, row);
        grid.GetBindingExpression(Selector.SelectedItemProperty)?.UpdateSource();
        await EventuallyAsync(() => !graph.Alerts.State.SelectionPending && graph.Alerts.State.Details?.EventId == row.EventId, ct);
        Require(graph.Alerts.State.Details?.Source.Trigger == row.Source.Trigger, "ui_alert_details_identity_changed");
        await RequireReachableAsync(BoundButton(window, graph.Alerts.RecipientFirstCommand), window, "ui_alert_recipient_control_unreachable");
        await InvokeCommandAsync(window, graph.Alerts.RecipientFirstCommand, ct);
        await RequireReachableAsync(BoundButton(window, graph.Alerts.AttemptFirstCommand), window, "ui_alert_record_control_unreachable");
        await InvokeCommandAsync(window, graph.Alerts.AttemptFirstCommand, ct);
        var state = graph.Alerts.State;
        Require(!state.RecipientsStale && !state.AttemptsStale && state.Recipients.Length == 1 && state.Attempts.Length == 0
            && !state.HasNextRecipients && !state.HasNextAttempts, "ui_alert_detail_bounds_incorrect");
        Require(state.Recipients[0].Source.SubscriptionId == proof.WatchId
            && state.Recipients[0].Source.Generation == 1 && state.Recipients[0].WatchLabel == proof.Label,
            "ui_alert_original_recipient_changed");
        Require(Visuals<ItemsControl>(window).Any(control => control.IsVisible && control.Items.Count == 1
                && control.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path?.Path == "State.Recipients")
            && Visuals<ItemsControl>(window).Any(control => control.IsVisible && control.Items.Count == 0
                && control.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.ParentBinding.Path?.Path == "State.Attempts"),
            "ui_alert_detail_bindings_missing");
        captures.Add(await CaptureAsync(profile, window, "ui-alerts-en-normal.png", false, ct));
        await SaveLanguageAsync(window, graph, UiLanguage.Korean, ct);
        await NavigateAsync(window, graph, StudentPage.Alerts);
        Require((window.TryFindResource("Ui.AlertOriginalSeats") as string) == "당시 여석",
            "ui_korean_alert_seat_label_incorrect");
        captures.Add(await CaptureAsync(profile, window, "ui-alerts-ko-narrow.png", true, ct));
        captures.Add(await CaptureAsync(profile, window, "ui-alerts-ko-details-narrow.png", true, ct));
        Require(source.Calls == sourceCalls, "ui_history_read_requested_source");
        return proof with { SavedAlertHistory = true, OriginalAlertIdentitySeats = true, BoundedAlertDetails = true };
    }
}
