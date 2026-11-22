using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Core;
using SnuWatcher.Windows;
using SnuWatcher.Windows.Hosting;
using SnuWatcher.Windows.Validation;

namespace SnuWatcher.Desktop.Runtime;

/// <summary>Owns the installed native qualification lifetime. Normal database files are never opened.</summary>
internal sealed partial class NativeDeliveryValidationRun(InstalledNativeDeliveryProfile claimed, string executable)
{
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(150));
    private SingleInstanceCoordinator? _instance;
    private DatabaseOwnerGate? _normalOwner;
    private NativeNotificationService? _native;
    private NativeDeliveryFixtureDriver? _driver;
    private NativeDeliveryValidationChannel? _channel;
    private WindowsMonitoringHost? _host;
    private readonly NativeDeliveryFixtureTransport _first = new();
    private NativeDeliveryFixtureTransport? _second;
    private UiPreferencesSnapshot? _saved;
    private AlertHistoryItem? _receipt;
    private AlertBatchAttempt? _attempt;
    private Guid _watchId;
    private BatchChannelReadiness _readiness;
    private bool _baseline, _reopen, _finished;
    private readonly object _cancelSync = new();
    private Task? _cancel;

    internal async Task ExecuteAsync()
    {
        // Read-only installer shortcut verification occurs before native construction/registration.
        var shortcut = NativeShortcutBinding.ReadExistingForValidation();
        if (shortcut.AppId != NativeAppIdentity.AppId || !string.Equals(Path.GetFullPath(shortcut.Target),
            Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("native_installed_target_mismatch");
        _instance = new(NativeAppIdentity.AppId);
        if (!_instance.TryAcquire()) throw new InvalidOperationException("native_normal_instance_owned");
        _instance.StartListening(request => { if (request.Action == ActivationAction.Quit) RequestStop(); });
        var owner = await DatabaseOwnerGate.TryAcquireAsync(NativeAppIdentity.AppId, _deadline.Token);
        _normalOwner = owner.Lease ?? throw new InvalidOperationException("native_normal_database_owned");
        _native = new(driver => _driver = new NativeDeliveryFixtureDriver(driver, claimed.Intent.Language));
        _native.Initialize();
        _channel = new(_native);
        _host = await OpenAsync(_first);
        _native.AvailabilityChanged += OnAvailabilityChanged;
        await SavePreferencesAsync(_host);
        _readiness = (await _native.ReadStatusAsync(_deadline.Token)).Readiness;
        var expected = claimed.Intent.Case == InstalledNativeDeliveryCase.PermissionDisabled ? BatchChannelReadiness.Disabled : BatchChannelReadiness.Ready;
        if (_readiness != expected) throw new InvalidOperationException("native_case_permission_precondition_failed");
        _watchId = Guid.NewGuid();
        await _host.Session.CreateAsync(new(_watchId, "TEST fixture opening — not live SNU", SubscriptionKind.Section,
            [NativeDeliveryFixtureTransport.Key], false, null, DateTimeOffset.UtcNow), _deadline.Token);
        await WaitAsync(async () => (await SectionAsync())?.Baseline?.Seats == new SeatCount(40, 40));
        if (!(await _host.Alerts.ReadPageAsync(cancellationToken: _deadline.Token)).Items.IsEmpty || _driver!.ShowCalls != 0 || _channel.Calls != 0)
            throw new InvalidOperationException("native_full_baseline_was_not_quiet");
        _baseline = true;
        _channel.ObserveOpening(); _first.OpenOneSeat();
        await _host.Session.RefreshAsync(NativeDeliveryFixtureTransport.Key, _deadline.Token);
        await WaitAsync(async () => (await SectionAsync())?.Baseline?.Seats == new SeatCount(39, 40));
        await WaitForResultAsync();
        await ReopenAsync();
    }

    private async Task<WindowsMonitoringHost> OpenAsync(NativeDeliveryFixtureTransport transport)
    {
        try
        {
            var opened = await WindowsMonitoringHost.OpenOfflineSnuValidationAsync(claimed.Profile, () => transport, _channel!,
                options: new RuntimeSessionOptions(monitor: new(jitterPercent: 0)), cancellationToken: _deadline.Token);
            return opened.Host ?? throw new InvalidOperationException("native_validation_private_owner_busy");
        }
        catch (WindowsMonitoringHostStartupException retained) { _host = retained.Host; throw; }
    }
    private async Task SavePreferencesAsync(WindowsMonitoringHost host)
    {
        var saved = await host.Preferences.TrySaveAsync(new(Guid.NewGuid(), 0, new(claimed.Intent.Language, 2026,
            new(2026, "U000200002U000300002"), WatchMonitorProfile.Faster, UiCloseBehavior.Quit, 1,
            claimed.Intent.NotificationSoundMuted)), _deadline.Token);
        _saved = saved.CurrentSnapshot;
        if (!_native!.ApplySavedPreferences(_saved)) throw new InvalidOperationException("native_saved_preferences_not_applied");
        await host.RefreshProfile.SetRefreshProfileAsync(WatchMonitorProfile.Faster, _saved.Revision, _deadline.Token);
    }
    private async Task<SectionSnapshot?> SectionAsync() =>
        (await _host!.Dashboard.ReadWatchDetailsAsync(_watchId, _deadline.Token))?.Sections.Single();
    private async Task WaitAsync(Func<Task<bool>> predicate)
    { while (!await predicate()) await Task.Delay(100, _deadline.Token); }
    private void OnAvailabilityChanged() => _ = _host?.NotifyDeliveryChannelChanged();
    private void RequestStop() { lock (_cancelSync) _cancel ??= _deadline.CancelAsync(); }

    private async Task WaitForResultAsync()
    {
        if (_readiness == BatchChannelReadiness.Disabled) await _channel!.DisabledOpeningRead.WaitAsync(_deadline.Token);
        await WaitAsync(async () =>
        {
            var rows = (await _host!.Alerts.ReadPageAsync(cancellationToken: _deadline.Token)).Items;
            return rows.Length == 1 && rows[0].Delivery.Status == (_readiness == BatchChannelReadiness.Disabled ? DeliveryStatus.Pending : DeliveryStatus.OsAccepted);
        });
        _receipt = (await _host!.Alerts.ReadPageAsync(cancellationToken: _deadline.Token)).Items.Single();
        var attempts = await _host.Alerts.ReadAttemptsAsync(_receipt.EventId, cancellationToken: _deadline.Token);
        if (attempts is null || attempts.HasMore) throw new InvalidOperationException("native_fixture_attempt_history_invalid");
        var batchAttempts = attempts.Items.Where(item => item.BatchAttempt is not null).ToArray();
        if (_receipt.Trigger.PreviousSeats != 0 || _receipt.Trigger.CurrentSeats != 1 || _receipt.Trigger.HasInitialReason
            || !_receipt.Trigger.HasIncreaseReason || _receipt.Delivery.UnresolvedAttemptId is not null) throw new InvalidOperationException("native_fixture_opening_facts_invalid");
        if (_readiness == BatchChannelReadiness.Disabled)
        {
            if (_receipt.Delivery.Attempts != 0 || _channel!.Calls != 0 || _driver!.ShowCalls != 0 || batchAttempts.Length != 0)
                throw new InvalidOperationException("native_disabled_budget_or_submission_changed");
        }
        else
        {
            if (_receipt.Delivery.Attempts != 1 || _channel!.Calls != 1 || _driver!.ShowCalls != 1 || _driver.PlatformNotificationId is not > 0
                || _channel.Result?.Outcome != BatchChannelOutcome.OsAccepted || batchAttempts.Length != 1 || !batchAttempts[0].CoversEvent)
                throw new InvalidOperationException("native_acceptance_evidence_incomplete");
            _attempt = batchAttempts[0].BatchAttempt!;
            if (_attempt.Outcome != BatchAttemptResultOutcome.OsAccepted || _attempt.BatchId != _receipt.Delivery.BatchId
                || _attempt.AttemptId != _channel.Submission!.Envelope.AttemptId || _attempt.Content.CurrentCounts != new SeatCount(39, 40)
                || _driver.SoundMuted != claimed.Intent.NotificationSoundMuted) throw new InvalidOperationException("native_actual_result_not_preserved");
        }
    }

    private async Task ReopenAsync()
    {
        if (!await CloseHostAsync()) throw new InvalidOperationException("native_validation_stop_blocked");
        if (!_first.IsDisposed) throw new InvalidOperationException("native_fixture_transport_not_drained");
        _second = new(); _second.OpenOneSeat();
        _host = await OpenAsync(_second);
        await _host.Session.RefreshAsync(NativeDeliveryFixtureTransport.Key, _deadline.Token);
        await WaitAsync(async () => (await SectionAsync())?.Baseline?.AttemptSequence > _receipt!.Trigger.Ordinal);
        var rows = await _host.Alerts.ReadPageAsync(cancellationToken: _deadline.Token);
        if (rows.Items.Length != 1 || rows.Items[0] != _receipt || _second.OpenSearches < 1
            || _channel!.Calls != (_readiness == BatchChannelReadiness.Ready ? 1 : 0)) throw new InvalidOperationException("native_reopen_resend_or_receipt_changed");
        _reopen = true;
    }
}
