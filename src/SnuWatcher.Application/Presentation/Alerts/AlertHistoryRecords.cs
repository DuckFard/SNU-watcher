using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Presentation.Alerts;

/// <summary>One original recipient association and an optional current watch label, never an invented event-time label.</summary>
public sealed record AlertHistoryRecipient
{
    public AlertHistoryRecipient(Guid subscriptionId, long generation, OpeningReason reason, string? currentWatchLabel)
    {
        SubscriptionId = AlertHistoryValidation.Id(subscriptionId, nameof(subscriptionId));
        Generation = AlertHistoryValidation.Positive(generation, nameof(generation));
        Reason = AlertHistoryValidation.Defined(reason, nameof(reason));
        if (currentWatchLabel is not null && (string.IsNullOrWhiteSpace(currentWatchLabel)
            || currentWatchLabel.Length > PersistenceLimits.MaxLabelLength || currentWatchLabel.Any(char.IsControl)))
            throw new ArgumentException("A current watch label must be bounded.", nameof(currentWatchLabel));
        CurrentWatchLabel = currentWatchLabel;
    }
    public Guid SubscriptionId { get; }
    public long Generation { get; }
    public OpeningReason Reason { get; }
    public string? CurrentWatchLabel { get; }
}

/// <summary>Whole-batch scalar submission facts; eligible recipient arrays remain outside history pages.</summary>
public sealed record AlertBatchContent
{
    public AlertBatchContent(SectionKey key, SeatCount currentCounts, DateTimeOffset currentSuccessAtUtc,
        long currentSourceOrdinal, long coverageCount)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (currentCounts.ApparentSeats <= 0) throw new ArgumentException("A sealed attempt needs open counts.", nameof(currentCounts));
        Key = key; CurrentCounts = currentCounts; CurrentSuccessAtUtc = currentSuccessAtUtc.ToUniversalTime();
        CurrentSourceOrdinal = AlertHistoryValidation.Positive(currentSourceOrdinal, nameof(currentSourceOrdinal));
        CoverageCount = AlertHistoryValidation.Positive(coverageCount, nameof(coverageCount));
    }
    public SectionKey Key { get; }
    public SeatCount CurrentCounts { get; }
    public DateTimeOffset CurrentSuccessAtUtc { get; }
    public long CurrentSourceOrdinal { get; }
    public long CoverageCount { get; }
}

/// <summary>One associated batch reservation; absent durable result means unresolved, never OS accepted.</summary>
public sealed record AlertBatchAttempt
{
    public AlertBatchAttempt(Guid attemptId, Guid batchId, long sequence, DateTimeOffset preparedAtUtc,
        AlertBatchContent content, BatchAttemptResultOutcome? outcome, DateTimeOffset? recordedAtUtc)
    {
        AttemptId = AlertHistoryValidation.Id(attemptId, nameof(attemptId));
        BatchId = AlertHistoryValidation.Id(batchId, nameof(batchId));
        Sequence = AlertHistoryValidation.Positive(sequence, nameof(sequence));
        ArgumentNullException.ThrowIfNull(content);
        if (outcome is { } result) AlertHistoryValidation.Defined(result, nameof(outcome));
        if ((outcome is null) != (recordedAtUtc is null)) throw new ArgumentException("A known durable result requires its recorded time.");
        PreparedAtUtc = preparedAtUtc.ToUniversalTime(); Content = content; Outcome = outcome; RecordedAtUtc = recordedAtUtc?.ToUniversalTime();
    }
    public Guid AttemptId { get; }
    public Guid BatchId { get; }
    public long Sequence { get; }
    public DateTimeOffset PreparedAtUtc { get; }
    public AlertBatchContent Content { get; }
    public BatchAttemptResultOutcome? Outcome { get; }
    public DateTimeOffset? RecordedAtUtc { get; }
}

/// <summary>Batch-context and event-result record buckets; these records must not be counted as separate banners.</summary>
public sealed record AlertHistoryAttempt
{
    public AlertHistoryAttempt(Guid eventId, AlertBatchAttempt? batchAttempt, AlertEventResult? eventResult, bool coversEvent)
    {
        EventId = AlertHistoryValidation.Id(eventId, nameof(eventId));
        if ((batchAttempt is null) == (eventResult is null) || eventResult is not null && !coversEvent)
            throw new ArgumentException("A record has exactly one source; event revisions belong to this event.");
        BatchAttempt = batchAttempt; EventResult = eventResult; CoversEvent = coversEvent;
    }
    public Guid EventId { get; }
    public AlertAttemptOrigin Origin => BatchAttempt is null ? AlertAttemptOrigin.EventResult : AlertAttemptOrigin.BatchAttempt;
    public long Sequence => BatchAttempt?.Sequence ?? EventResult!.Revision;
    /// <summary>Associated whole-batch context; a later attempt can cover other events without covering this one.</summary>
    public AlertBatchAttempt? BatchAttempt { get; }
    public AlertEventResult? EventResult { get; }
    public bool CoversEvent { get; }
}
