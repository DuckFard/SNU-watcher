using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Desktop.Presentation;
using SnuWatcher.Windows.Hosting;
using SnuWatcher.Windows.Presentation.Common;

namespace SnuWatcher.Desktop.Runtime;

/// <summary>Early private UI routing. A malformed validation prefix can never enter normal/native startup.</summary>
internal static partial class StudentUiValidation
{
    private const string Prefix = "--ui-validation";
    private static string _stage = "argument";
    private sealed record RetainedUiLifetime(WindowsMonitoringHost? Host, StudentDesktopSession? Graph, StudentWindow? Window);
    private static RetainedUiLifetime? _blocked;

    internal static async Task<bool> TryHandleAsync(string[] arguments, Action<int> shutdown)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(shutdown);
        if (!arguments.Any(argument => argument?.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) == true))
            return false;
        if (arguments.Length != 1 || !arguments[0].StartsWith(Prefix + "=", StringComparison.Ordinal)
            || !Guid.TryParseExact(arguments[0][(Prefix.Length + 1)..], "N", out var runId)
            || runId == Guid.Empty || runId.ToString("N") != arguments[0][(Prefix.Length + 1)..])
        {
            WritePrivateError("invalid_ui_validation_argument", "REJECTED");
            shutdown(2);
            return true;
        }
        await RunAsync(runId, shutdown);
        return true;
    }

    private static async Task RunAsync(Guid runId, Action<int> shutdown)
    {
        RuntimeProfile? profile = null;
        WindowsMonitoringHost? host = null;
        StudentDesktopSession? graph = null;
        StudentWindow? window = null;
        StudentUiWorkArea? workArea = null;
        var clock = new StudentUiValidationClock();
        var source = new StudentUiValidationSource(clock);
        var channel = new StudentUiValidationChannel();
        var actions = new StudentUiValidationActions();
        var simulatedNative = new StudentUiSimulatedNativeControls(clock);
        var captures = new List<StudentUiCapture>();
        using var bindings = new StudentUiBindingProbe();
        var elapsed = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(StudentUiValidationFailureDiagnostics.BudgetSeconds));
        try
        {
            _stage = "profile";
            profile = RuntimeProfile.OpenValidationRun(runId);
            System.Windows.Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var options = new RuntimeSessionOptions(deliveryEnabled: false);
            var opened = await WindowsMonitoringHost.OpenValidationAsync(profile, () => source, channel, clock, options, deadline.Token);
            host = opened.Host ?? throw new InvalidOperationException("ui_validation_owner_already_owned");
            var dispatcher = new WpfUiDispatcher(System.Windows.Application.Current.Dispatcher);
            graph = new(host, dispatcher, clock, actions, actions.NativeTestGuard, isDemo: true, observeSystemState: false,
                nativeNotifications: simulatedNative);
            graph.Settings.PreferencesChanged += simulatedNative.ApplyKnownPreferences;
            var quitRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window = new(graph.Shell, () => quitRequested.TrySetResult(), enableTray: false) { ShowInTaskbar = false };
            System.Windows.Application.Current.MainWindow = window;
            window.Show(); window.Activate();
            await PumpUiAsync(window);
            workArea = ReadStartupWorkArea(window);
            Require(workArea.StartupBoundsWithinWorkArea, "ui_startup_window_outside_work_area");
            _stage = "initialization";
            var initialPreferenceReads = 0;
            Action<SnuWatcher.Application.Presentation.Preferences.UiPreferencesSnapshot> initialRead = _ => initialPreferenceReads++;
            graph.Settings.PreferencesChanged += initialRead;
            var first = graph.InitializeAsync(deadline.Token);
            var joined = graph.InitializeAsync(deadline.Token);
            var initial = await Task.WhenAll(first, joined);
            graph.Settings.PreferencesChanged -= initialRead;
            Require(initial.All(result => result == UiCommandOutcome.Completed) && source.Calls == 0 && initialPreferenceReads == 1,
                "ui_local_initialization_requested_source");
            await PumpUiAsync(window);
            _stage = "glyph_guard_self_checks";
            await ProbeGlyphGuardsAsync(window);
            _stage = "journey";
            var journey = await RunJourneyAsync(profile, window, graph, host, source, clock, actions, bindings,
                simulatedNative, captures, deadline.Token);
            workArea = await VerifyActualWorkAreaAsync(profile, captures, window, graph, source, journey, workArea,
                progress => workArea = progress, deadline.Token);
            journey = journey with { PreferencesRevision = graph.Settings.Model.State.Stored!.Revision };
            Require(bindings.ErrorCount == 0, "ui_binding_errors_detected");
            _stage = "quit_drain";
            var stopped = await QuitAndDrainAsync(profile, window, graph, host, source, quitRequested, deadline.Token);
            Require(stopped.OwnerRetained && stopped.Drained && source.IsDisposed, "ui_shutdown_proof_incomplete");
            window.AllowClose(); window.Close(); window.Dispose(); window = null;
            host = null; graph = null;
            _stage = "reopen";
            var stable = await VerifyUiReopenAsync(profile, journey, clock, options, deadline.Token);
            Require(stable.OwnerHandoff && stable.PendingAlerts >= 1 && channel.Calls == 0 && actions.NativeTestRequests == 0,
                "ui_reopen_or_delivery_proof_incomplete");
            Require(bindings.ErrorCount == 0, "ui_binding_errors_detected");
            _stage = "result";
            WriteUiResult(profile, new
            {
                schemaVersion = 2, codePath = "ui-validation", testStatus = "PASSED", code = "student_ui_validation_passed",
                elapsedMilliseconds = elapsed.ElapsedMilliseconds, budgetSeconds = StudentUiValidationFailureDiagnostics.BudgetSeconds,
                runId = runId.ToString("N"), processId = Environment.ProcessId, profileGuarded = true, isDemo = true,
                noLiveHttp = true, httpRequests = 0, noOsNotifications = true, nativeNotificationCalls = 0,
                noBrowser = true, browserCalls = 0, noClipboard = true, clipboardCalls = 0,
                noSimulatedAcceptance = true, noSimulatedAcceptanceScope = "durable-production-delivery-only",
                noSimulatedDeliveryAcceptance = true, deliveryDisabled = true, channelCalls = channel.Calls,
                simulatedNativeTest = new { driver = "deterministic-offline-controls", simulatedNative.StatusProbes,
                    fixtureAcceptedReceipts = simulatedNative.SimulatedTests, programmaticConfirmations = 2,
                    observationScope = "programmatic-ui-only-no-human-seen-or-heard-evidence", realNativeCalls = 0,
                    legacyGuardCalls = actions.NativeTestRequests },
                localStartupOnly = true, initializationJoined = true, graphDrained = true, hostDrained = true,
                reopenStable = true, ownerHandoff = true, ownerRetainedDuringDrain = stopped.OwnerRetained,
                sourceDisposed = source.IsDisposed, quitObserved = true, pendingAlertsPreserved = stable.PendingAlerts,
                bindingErrors = bindings.ErrorCount, bindingEvidence = bindings.Errors,
                expectedYearValidationTraces = bindings.ExpectedValidationCount,
                glyphGuardsVerified = true,
                actualWorkAreaVerified = true, workArea,
                keyboardScope = "in-process WPF focus and routed events", journey, captures,
                source = new { source.CatalogCalls, source.InteractiveSearchCalls, source.WatchSearchCalls, source.DetailsCalls },
                recordedActions = new { actions.RegistrationIntents, actions.GenieIntents, actions.CopyIntents },
            });
            shutdown(0);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Observe the original failure before diagnostic rendering/drain can consume time or expire the deadline.
            var failureElapsedMilliseconds = elapsed.ElapsedMilliseconds;
            var deadlineCancelled = deadline.IsCancellationRequested;
            var failureKind = StudentUiValidationFailureDiagnostics.Classify(error, deadlineCancelled, deadline.Token);
            if (error is WindowsMonitoringHostStartupException retained) host = retained.Host;
            if (error is StudentUiRetainedHostException blocked) host = blocked.Host;
            if (profile is not null && window?.IsVisible == true)
            {
                try { captures.Add(WriteCapture(profile, window, "ui-failure-layout.png")); }
                catch (Exception captureFailure) when (captureFailure is IOException or UnauthorizedAccessException or InvalidOperationException)
                { /* A failed diagnostic capture must not hide the original failure or prevent actual drain. */ }
            }
            source.ReleaseDetails();
            var drained = await TryCleanupUiAsync(graph, host);
            if (drained && window is not null) { window.AllowClose(); window.Close(); window.Dispose(); }
            var code = error is InvalidOperationException && StudentUiValidationFailureDiagnostics.IsPrivateUiCode(error.Message)
                ? error.Message : "student_ui_validation_failed";
            if (profile is not null)
            {
                try
                {
                    WriteUiResult(profile, new
                    {
                        schemaVersion = 2, codePath = "ui-validation", testStatus = "FAILED", code, stage = _stage,
                        failureKind = failureKind.ToString(), deadlineCancelled, elapsedMilliseconds = failureElapsedMilliseconds,
                        budgetSeconds = StudentUiValidationFailureDiagnostics.BudgetSeconds,
                        runId = runId.ToString("N"), processId = Environment.ProcessId, profileGuarded = true,
                        noLiveHttp = true, httpRequests = 0, noOsNotifications = true, nativeNotificationCalls = 0,
                        noBrowser = true, browserCalls = 0, noClipboard = true, clipboardCalls = 0, channelCalls = channel.Calls,
                        graphDrained = drained, hostDrained = drained, bindingErrors = bindings.ErrorCount,
                        bindingEvidence = bindings.Errors, expectedYearValidationTraces = bindings.ExpectedValidationCount, captures,
                        workArea,
                        source = new { source.CatalogCalls, source.InteractiveSearchCalls, source.WatchSearchCalls, source.DetailsCalls },
                    });
                }
                catch (Exception writeFailure) when (writeFailure is IOException or UnauthorizedAccessException or InvalidOperationException)
                { WritePrivateError("ui_validation_result_write_failed"); }
            }
            else WritePrivateError("invalid_ui_validation_profile");
            if (drained) shutdown(2);
            else Interlocked.Exchange(ref _blocked, new(host, graph, window)); // Actual work/owner survives until the owned script deadline.
        }
    }

    private static void WriteUiResult(RuntimeProfile profile, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Require(bytes.Length is > 0 and <= 65536, "ui_result_limit");
        profile.ValidateExisting();
        using var stream = new FileStream(profile.ResultPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Write(bytes); stream.Flush(flushToDisk: true);
    }

    private static void WritePrivateError(string code, string status = "FAILED")
    {
        try { Console.Error.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 2, codePath = "ui-validation",
            testStatus = status, code, nativeNotificationCalls = 0, httpRequests = 0, browserCalls = 0, clipboardCalls = 0 })); }
        catch (System.IO.IOException) { /* A closed private pipe never permits native fallthrough. */ }
    }
}
