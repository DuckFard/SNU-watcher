using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Presentation.Alerts;

/// <summary>Original immutable opening facts; apparent seats and timestamps are never refreshed from today's section state.</summary>
public sealed record AlertTriggerFacts
{
    public AlertTriggerFacts(Guid eventId, SectionKey key, long ordinal, int currentSeats, int? previousSeats,
        DateTimeOffset observedAtUtc, DateTimeOffset createdAtUtc, int recipientCount, bool hasInitialReason, bool hasIncreaseReason)
    {
        EventId = AlertHistoryValidation.Id(eventId, nameof(eventId));
        ArgumentNullException.ThrowIfNull(key); Key = key;
        Ordinal = AlertHistoryValidation.Positive(ordinal, nameof(ordinal));
        if (currentSeats <= 0 || previousSeats < 0) throw new ArgumentOutOfRangeException(nameof(currentSeats));
        if (recipientCount is < 1 or > PersistenceLimits.MaxSections || !hasInitialReason && !hasIncreaseReason)
            throw new ArgumentException("Original opening recipients and reasons must be present and bounded.", nameof(recipientCount));
        CurrentSeats = currentSeats; PreviousSeats = previousSeats; RecipientCount = recipientCount;
        ObservedAtUtc = observedAtUtc.ToUniversalTime(); CreatedAtUtc = createdAtUtc.ToUniversalTime();
        HasInitialReason = hasInitialReason; HasIncreaseReason = hasIncreaseReason;
    }
    public Guid EventId { get; }
    public SectionKey Key { get; }
    public long Ordinal { get; }
    public int CurrentSeats { get; }
    public int? PreviousSeats { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public int RecipientCount { get; }
    public bool HasInitialReason { get; }
    public bool HasIncreaseReason { get; }
}

/// <summary>A durable event-delivery revision, including logical or batch-generated results. It is not a distinct OS call.</summary>
public sealed record AlertEventResult
{
    public AlertEventResult(long revision, DeliveryStatus status, int attempts, DateTimeOffset recordedAtUtc)
    {
        Revision = AlertHistoryValidation.Positive(revision, nameof(revision));
        Status = AlertHistoryValidation.Defined(status, nameof(status));
        ArgumentOutOfRangeException.ThrowIfNegative(attempts);
        Attempts = attempts; RecordedAtUtc = recordedAtUtc.ToUniversalTime();
    }
    public long Revision { get; }
    public DeliveryStatus Status { get; }
    public int Attempts { get; }
    public DateTimeOffset RecordedAtUtc { get; }
}

/// <summary>Current durable delivery state, distinct from original trigger facts. OS acceptance does not prove visibility or registration.</summary>
public sealed record AlertDeliveryState
{
    public AlertDeliveryState(long revision, DeliveryStatus status, int attempts, Guid? batchId,
        Guid? unresolvedAttemptId, AlertEventResult? lastRecordedResult)
    {
        Revision = AlertHistoryValidation.Positive(revision, nameof(revision));
        Status = AlertHistoryValidation.Defined(status, nameof(status));
        ArgumentOutOfRangeException.ThrowIfNegative(attempts);
        if (batchId is { } batch) AlertHistoryValidation.Id(batch, nameof(batchId));
        if (unresolvedAttemptId is { } unresolved) AlertHistoryValidation.Id(unresolved, nameof(unresolvedAttemptId));
        if (unresolvedAttemptId is not null && batchId is null || lastRecordedResult is { } last
            && (last.Revision > revision || last.Attempts > attempts))
            throw new ArgumentException("Delivery evidence must agree with its durable association.");
        Attempts = attempts; BatchId = batchId; UnresolvedAttemptId = unresolvedAttemptId; LastRecordedResult = lastRecordedResult;
    }
    public long Revision { get; }
    public DeliveryStatus Status { get; }
    public int Attempts { get; }
    public Guid? BatchId { get; }
    /// <summary>Set only when this exact event belongs to the unresolved attempt's frozen coverage.</summary>
    public Guid? UnresolvedAttemptId { get; }
    /// <summary>Legacy status records do not disclose a missing native outcome; no such outcome is inferred.</summary>
    public AlertEventResult? LastRecordedResult { get; }
}

/// <summary>One historical change and its current durable delivery status; no eager recipient or attempt arrays.</summary>
public sealed record AlertHistoryItem
{
    public AlertHistoryItem(AlertTriggerFacts trigger, AlertDeliveryState delivery)
    { ArgumentNullException.ThrowIfNull(trigger); ArgumentNullException.ThrowIfNull(delivery); Trigger = trigger; Delivery = delivery; }
    public AlertTriggerFacts Trigger { get; }
    public AlertDeliveryState Delivery { get; }
    public Guid EventId => Trigger.EventId;
}
