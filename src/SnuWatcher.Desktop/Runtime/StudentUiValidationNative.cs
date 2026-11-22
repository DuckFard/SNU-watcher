using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Desktop.Presentation;
using SnuWatcher.Windows.Hosting;
using SnuWatcher.Windows.Notifications;
using SnuWatcher.Windows.Presentation.Settings;
using SnuWatcher.Windows.Presentation.Shell;

namespace SnuWatcher.Desktop.Runtime;

/// <summary>In-memory UI branch fixture; it has no SDK, native identity, notification or HTTP implementation.</summary>
internal sealed class StudentUiSimulatedNativeControls(StudentUiValidationClock clock) : INativeNotificationControls
{
    private UiPreferencesSnapshot? saved;
    private int ready, probes, tests;
    internal int StatusProbes => Volatile.Read(ref probes);
    internal int SimulatedTests => Volatile.Read(ref tests);
    internal UiPreferencesSnapshot? KnownPreferences => Volatile.Read(ref saved);
    internal void ApplyKnownPreferences(UiPreferencesSnapshot snapshot) => Volatile.Write(ref saved, snapshot);
    internal void EnableFixture() => Volatile.Write(ref ready, 1);
    public Task<NativeNotificationStatus> ReadStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref probes);
        var enabled = Volatile.Read(ref ready) != 0;
        return Task.FromResult(new NativeNotificationStatus(enabled ? BatchChannelReadiness.Ready : BatchChannelReadiness.Disabled,
            enabled, clock.GetUtcNow()));
    }
    public Task<NativeNotificationTestReceipt> SendTestAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref ready) == 0 || KnownPreferences?.Values.NotificationSoundMuted != true)
            throw new InvalidOperationException("ui_simulated_test_requires_known_saved_mute");
        var sequence = Interlocked.Increment(ref tests);
        if (sequence > 2) throw new InvalidOperationException("ui_simulated_test_limit_exceeded");
        var id = Guid.ParseExact("bbbbbbbbbbbbbbbbbbbb" + sequence.ToString("x12", System.Globalization.CultureInfo.InvariantCulture), "N");
        // This typed fixture response exercises UI branches only. Nothing is sent to Windows or durable delivery.
        return Task.FromResult(new NativeNotificationTestReceipt(id, new(BatchChannelOutcome.OsAccepted),
            (uint)sequence, clock.GetUtcNow()));
    }
}

internal static partial class StudentUiValidation
{
    private static async Task<StudentUiJourney> VerifySimulatedNativeUiAsync(RuntimeProfile profile, StudentWindow window,
        StudentDesktopSession graph, StudentUiSimulatedNativeControls simulated, StudentUiJourney proof,
        List<StudentUiCapture> captures, CancellationToken ct)
    {
        _stage = "simulated_native_status";
        await NavigateAsync(window, graph, StudentPage.Settings);
        Require(graph.Settings.Model.State.NativeStatus?.Readiness == BatchChannelReadiness.Disabled
            && !BoundButton(window, graph.Settings.NativeTestCommand).IsEnabled && simulated.SimulatedTests == 0,
            "ui_simulated_disabled_status_incorrect");
        var mute = Named<CheckBox>(window, "MuteNotificationSoundField");
        mute.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        mute.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource();
        Require(graph.Settings.NotificationSoundMuted && simulated.KnownPreferences?.Values.NotificationSoundMuted == false,
            "ui_unsaved_mute_was_applied");
        await InvokeCommandAsync(window, graph.Settings.SaveCommand, ct);
        Require(simulated.KnownPreferences?.Values.NotificationSoundMuted == true
            && simulated.KnownPreferences.Revision == graph.Settings.Model.State.Stored?.Revision,
            "ui_mute_not_known_saved");
        simulated.EnableFixture();
        await InvokeCommandAsync(window, graph.Settings.RefreshNativeStatusCommand, ct);
        Require(graph.Settings.Model.State.NativeStatus?.Readiness == BatchChannelReadiness.Ready
            && BoundButton(window, graph.Settings.NativeTestCommand).IsEnabled, "ui_simulated_ready_status_incorrect");

        _stage = "simulated_native_feedback";
        await InvokeCommandAsync(window, graph.Settings.NativeTestCommand, ct);
        var first = graph.Settings.Model.State.NativeTestReceipt?.TestId ?? throw new InvalidOperationException("ui_simulated_receipt_missing");
        Require(graph.Settings.Model.State.NativeTestFeedback?.Confirmation == NativeTestBannerConfirmation.Unknown,
            "ui_simulated_acceptance_claimed_seen");
        InvokeButton(ConfirmationButton(window, first, NativeTestBannerConfirmation.Seen)); await PumpUiAsync(window);
        Require(graph.Settings.Model.State.NativeTestFeedback?.Confirmation == NativeTestBannerConfirmation.Seen,
            "ui_simulated_seen_control_failed");
        await InvokeCommandAsync(window, graph.Settings.NativeTestCommand, ct);
        var second = graph.Settings.Model.State.NativeTestReceipt?.TestId ?? throw new InvalidOperationException("ui_simulated_receipt_missing");
        Require(first != second && graph.Settings.Model.State.NativeTestFeedback?.Confirmation == NativeTestBannerConfirmation.Unknown
            && !graph.Settings.ConfirmNativeTest(first, NativeTestBannerConfirmation.Seen), "ui_simulated_confirmation_scope_failed");
        InvokeButton(ConfirmationButton(window, second, NativeTestBannerConfirmation.NotSeen)); await PumpUiAsync(window);
        Require(graph.Settings.Model.State.NativeTestFeedback is { Confirmation: NativeTestBannerConfirmation.NotSeen } feedback
            && feedback.TestId == second && simulated.SimulatedTests == 2, "ui_simulated_not_seen_control_failed");
        captures.Add(await CaptureAsync(profile, window, "ui-simulated-native-en.png", false, ct));
        await SaveLanguageAsync(window, graph, UiLanguage.Korean, ct);
        captures.Add(await CaptureAsync(profile, window, "ui-simulated-native-ko-narrow.png", true, ct));
        await SaveLanguageAsync(window, graph, UiLanguage.English, ct);
        return proof with { SavedSoundMute = true, SimulatedNativeStatus = true,
            SimulatedNativeTest = true, SimulatedFeedbackScoped = true };
    }

    private static Button ConfirmationButton(Window window, Guid testId, NativeTestBannerConfirmation confirmation) =>
        Visuals<Button>(window).Single(button => button.IsVisible && button.Tag is Guid id && id == testId
            && button.CommandParameter is NativeTestBannerConfirmation value && value == confirmation);
}
