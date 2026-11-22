using System;
using System.Threading.Tasks;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Windows;
using SnuWatcher.Windows.Hosting;
using SnuWatcher.Windows.Validation;

namespace SnuWatcher.Desktop.Runtime;

internal sealed partial class NativeDeliveryValidationRun
{
    private async Task<bool> CloseHostAsync()
    {
        if (_host is null) return true;
        var stopped = await _host.StopAsync();
        for (var retry = 0; retry < 2 && !stopped.IsFinalized; retry++) stopped = await _host.RetryFinalizeStopAsync();
        if (!stopped.IsFinalized) return false;
        await _host.DisposeAsync(); _host = null;
        return true;
    }

    internal async Task<bool> FinishAsync()
    {
        if (_finished) return true;
        try
        {
            // No timeout or cancellation abandons a synchronous Show. Stop and native teardown await
            // their actual retained workers; only then can normal/private owners be released.
            if (!await CloseHostAsync()) return false;
            if (_native is not null)
            {
                _native.AvailabilityChanged -= OnAvailabilityChanged;
                await _native.DisposeAsync(); _native = null;
            }
            _first.Dispose(); _second?.Dispose();
            if (_instance is not null) { await _instance.DisposeAsync(); _instance = null; }
            if (_normalOwner is not null) { await _normalOwner.DisposeAsync(); _normalOwner = null; }
            if (_cancel is not null) await _cancel;
            _deadline.Dispose();
            _finished = true;
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    internal async Task<bool> VerifyHandoffAsync()
    {
        if (!_finished) return false;
        var privateOwner = await DatabaseOwnerGate.TryAcquireAsync(claimed.Profile.ApplicationId);
        if (privateOwner.Lease is null) return false;
        await privateOwner.Lease.DisposeAsync();
        var normalOwner = await DatabaseOwnerGate.TryAcquireAsync(NativeAppIdentity.AppId);
        if (normalOwner.Lease is null) return false;
        await normalOwner.Lease.DisposeAsync();
        await using var instance = new SingleInstanceCoordinator(NativeAppIdentity.AppId);
        return instance.TryAcquire();
    }

    internal object SuccessEvidence()
    {
        if (!_finished || !_baseline || !_reopen || _receipt is null || _saved is null || _driver is null || _channel is null
            || !_first.IsDisposed || _second?.IsDisposed != true || _first.RejectedRequests + _second.RejectedRequests != 0)
            throw new InvalidOperationException("native_qualification_evidence_incomplete");
        var accepted = _readiness == BatchChannelReadiness.Ready;
        return new
        {
            schemaVersion = 1, codePath = "native-delivery-validation", testStatus = "PASSED", code = "installed_native_delivery_passed",
            runId = claimed.Intent.RunId.ToString("N"), @case = claimed.Intent.CaseName, processId = Environment.ProcessId,
            executableSha256 = claimed.Intent.ExecutableSha256, pipelineSha256 = claimed.Intent.PipelineSha256,
            fixtureOnly = true, liveHttpRequests = 0, fixtureHttpRequests = _first.Requests + _second.Requests,
            rejectedFixtureRequests = _first.RejectedRequests + _second.RejectedRequests,
            fullBaselineCommitted = _baseline, initialEvents = 0, openingCommitted = true, noResendOnReopen = _reopen,
            nativeShowCalls = _driver.ShowCalls, channelSendCalls = _channel.Calls,
            nativePlatformId = accepted ? _driver.PlatformNotificationId : null,
            channelOutcome = accepted ? _channel.Result?.Outcome.ToString() : null,
            eventStatus = _receipt.Delivery.Status.ToString(), chargedAttempts = _receipt.Delivery.Attempts,
            unresolvedAttempt = _receipt.Delivery.UnresolvedAttemptId.HasValue, readiness = _readiness.ToString(),
            disabledOpeningReadObserved = _channel.DisabledOpeningRead.IsCompletedSuccessfully,
            savedLanguage = _saved.Values.Language.ToString(), savedSoundMuted = _saved.Values.NotificationSoundMuted,
            nativePayloadSha256 = _driver.PayloadSha256, nativeSoundMuted = accepted ? (bool?)_driver.SoundMuted : null,
            eventId = _receipt.EventId.ToString("N"), batchId = _receipt.Delivery.BatchId?.ToString("N"), attemptId = _attempt?.AttemptId.ToString("N"),
            triggerObservedAtUtc = _receipt.Trigger.ObservedAtUtc, nativeResultRecordedAtUtc = _attempt?.RecordedAtUtc,
            workersDrained = true, ownerHandoff = true, visibleBannerConfirmed = false, soundConfirmed = false,
            buttonClickConfirmed = false, bodyClickConfirmed = false, cleanVmGatePassed = false,
        };
    }
}
