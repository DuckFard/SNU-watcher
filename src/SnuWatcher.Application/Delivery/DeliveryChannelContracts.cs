using SnuWatcher.Application.Persistence;

namespace SnuWatcher.Application.Delivery;

/// <summary>OS acceptance does not prove a visible banner, a click, or registration.</summary>
public enum DesktopAlertOutcome { RetryableFailure, OsAccepted }

/// <summary>
/// Sends one attempt using the envelope's stable native deduplication key. Implementations return
/// the actual desktop result; a successful diagnostic or audit write is not desktop acceptance.
/// A driver must bound its operation and finish or cancel the actual send before completing this task;
/// returning cancellation while an OS submission continues would release the lifecycle gate too early.
/// Once OS acceptance is known, return OsAccepted even if caller cancellation arrived meanwhile.
/// Native deadlines belong to the driver; the coordinator never abandons an in-flight send to release its gate.
/// </summary>
public interface IDesktopAlertChannel
{
    Task<DesktopAlertOutcome> SendAsync(DeliveryEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>Channel and persistence results remain distinct, including an accepted but unrecorded send.</summary>
public sealed record DeliveryAttemptResult
{
    internal DeliveryAttemptResult(DeliveryDecision decision, DesktopAlertOutcome? channelOutcome,
        DeliveryWriteResult? writeResult, string reasonCode, bool observerFailed = false)
    {
        Decision = decision;
        ChannelOutcome = channelOutcome;
        WriteResult = writeResult;
        ReasonCode = reasonCode;
        ObserverFailed = observerFailed;
    }

    public DeliveryDecision Decision { get; }
    public DesktopAlertOutcome? ChannelOutcome { get; }
    public DeliveryWriteResult? WriteResult { get; }
    public string ReasonCode { get; }
    public bool ObserverFailed { get; }

    internal DeliveryAttemptResult WithObserverFailure() => new(Decision, ChannelOutcome, WriteResult, ReasonCode, true);
}

/// <summary>Optional local diagnostics; observing this value cannot acknowledge an alert.</summary>
public sealed record DeliveryAttemptAudit
{
    internal DeliveryAttemptAudit(Guid eventId, int attempts, DeliveryAttemptResult result, DateTimeOffset evaluatedAtUtc)
    {
        EventId = eventId;
        Attempts = attempts;
        ChannelOutcome = result.ChannelOutcome;
        WriteResult = result.WriteResult;
        ReasonCode = result.ReasonCode;
        EvaluatedAtUtc = evaluatedAtUtc.ToUniversalTime();
    }

    public Guid EventId { get; }
    public int Attempts { get; }
    public DesktopAlertOutcome? ChannelOutcome { get; }
    public DeliveryWriteResult? WriteResult { get; }
    public string ReasonCode { get; }
    public DateTimeOffset EvaluatedAtUtc { get; }
}
