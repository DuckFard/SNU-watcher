using SnuWatcher.Application.Persistence;

namespace SnuWatcher.Application.Delivery;

/// <summary>
/// Serializes current-state validation and sending with lifecycle changes that use the same gate.
/// Pause may await an in-flight send; after pause completes that generation cannot start another send.
/// Polling must not use this gate. Each store call finishes its transaction before any channel await.
/// </summary>
public sealed class DeliveryCoordinator
{
    private readonly IWatchStore _store;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _lifecycleGate;
    private readonly IDesktopAlertChannel _channel;
    private readonly Action<DeliveryAttemptAudit>? _observer;

    public DeliveryCoordinator(IWatchStore store, TimeProvider clock, SemaphoreSlim lifecycleGate,
        IDesktopAlertChannel channel, Action<DeliveryAttemptAudit>? observer = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(lifecycleGate);
        ArgumentNullException.ThrowIfNull(channel);
        _store = store;
        _clock = clock;
        _lifecycleGate = lifecycleGate;
        _channel = channel;
        _observer = observer;
    }

    public async Task<DeliveryAttemptResult> TryDeliverAsync(Guid eventId, TimeSpan maxAge, int maxAttempts,
        CancellationToken cancellationToken = default)
    {
        if (eventId == Guid.Empty) throw new ArgumentException("An event identity is required.", nameof(eventId));
        if (maxAge <= TimeSpan.Zero || maxAge > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(maxAge), "Maximum observation age must be positive and at most one day.");
        if (maxAttempts is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "Maximum delivery attempts must be between one and 100.");
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        DeliveryAttemptResult result;
        DeliveryAttemptAudit audit;
        try
        {
            var context = await _store.ReadDeliveryContextAsync(eventId, cancellationToken).ConfigureAwait(false);
            var decision = DeliveryPlanner.Plan(context, _clock.GetUtcNow(), maxAge);
            var execution = await ExecuteAsync(context.Delivery, decision, maxAttempts, cancellationToken).ConfigureAwait(false);
            result = execution.Result;
            audit = new(eventId, execution.Attempts, result, _clock.GetUtcNow());
        }
        finally { _lifecycleGate.Release(); }
        // Observers may reenter lifecycle operations. They never run under the send gate.
        try { _observer?.Invoke(audit); }
        catch (Exception) { return result.WithObserverFailure(); }
        return result;
    }

    private async Task<(DeliveryAttemptResult Result, int Attempts)> ExecuteAsync(PendingDelivery delivery,
        DeliveryDecision decision, int maxAttempts, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (decision.Disposition is DeliveryDisposition.Terminal or DeliveryDisposition.NeedsFreshCheck)
            return (new(decision, null, null, decision.ReasonCode), delivery.Attempts);
        if (decision.Disposition == DeliveryDisposition.Superseded)
            return (await RecordAsync(delivery, decision, DeliveryStatus.Superseded, delivery.Attempts,
                null, decision.ReasonCode, cancellationToken).ConfigureAwait(false), delivery.Attempts);
        if (delivery.Attempts >= maxAttempts)
            return (await RecordAsync(delivery, decision, DeliveryStatus.Failed, delivery.Attempts,
                null, "attempt_limit", cancellationToken).ConfigureAwait(false), delivery.Attempts);

        // The explicit check is immediately before invocation, even when the read finished after cancellation.
        cancellationToken.ThrowIfCancellationRequested();
        var channel = await SendAsync(decision.Envelope!, cancellationToken).ConfigureAwait(false);
        var attempts = checked(delivery.Attempts + 1);
        var status = channel.Outcome == DesktopAlertOutcome.OsAccepted ? DeliveryStatus.OsAccepted
            : attempts >= maxAttempts ? DeliveryStatus.Failed : DeliveryStatus.Pending;
        // A completed channel result is authoritative. Caller cancellation after it must not discard the
        // acknowledgement or retry count. Store execution itself preserves committed results atomically.
        return (await RecordAsync(delivery, decision, status, attempts, channel.Outcome, channel.Reason,
            CancellationToken.None).ConfigureAwait(false), attempts);
    }

    private async Task<(DesktopAlertOutcome Outcome, string Reason)> SendAsync(DeliveryEnvelope envelope,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _channel.SendAsync(envelope, cancellationToken).ConfigureAwait(false) switch
            {
                DesktopAlertOutcome.OsAccepted => (DesktopAlertOutcome.OsAccepted, "os_accepted"),
                DesktopAlertOutcome.RetryableFailure => (DesktopAlertOutcome.RetryableFailure, "desktop_retryable_failure"),
                _ => (DesktopAlertOutcome.RetryableFailure, "invalid_desktop_result"),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return (DesktopAlertOutcome.RetryableFailure, "desktop_error"); }
    }

    private async Task<DeliveryAttemptResult> RecordAsync(PendingDelivery delivery, DeliveryDecision decision,
        DeliveryStatus status, int attempts, DesktopAlertOutcome? outcome, string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var write = await _store.RecordDeliveryResultAsync(new(delivery.EventId, delivery.DeliveryRevision,
                status, attempts, _clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            return new(decision, outcome, write, write switch
            {
                DeliveryWriteResult.Applied => reason,
                DeliveryWriteResult.VersionConflict => "result_version_conflict",
                _ => "result_not_found",
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(decision, outcome, null, "result_not_recorded"); }
    }
}
