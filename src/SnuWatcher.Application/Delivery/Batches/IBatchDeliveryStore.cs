using System.Collections.Immutable;
using SnuWatcher.Application.Persistence;

namespace SnuWatcher.Application.Delivery.Batches;

public enum BatchReservationStatus
{
    Reserved, Replayed, UnresolvedAttempt, NeedsFreshCheck, NeedsRevalidation, MoreToFold, Closed, VersionConflict, RecipientLimitReached
}

public sealed record BatchReservationResult
{
    public BatchReservationResult(BatchReservationStatus status, DeliveryBatchSnapshot batch, BatchAttemptSnapshot? attempt,
        Guid? unresolvedAttemptId, string reasonCode, BatchAttemptReceipt? resolution = null)
    {
        ArgumentNullException.ThrowIfNull(batch);
        Status = PersistenceValidation.Defined(status, nameof(status));
        UnresolvedAttemptId = BatchValidation.OptionalId(unresolvedAttemptId, nameof(unresolvedAttemptId));
        var hasAttempt = status is BatchReservationStatus.Reserved or BatchReservationStatus.Replayed;
        if (hasAttempt != (attempt is not null) || (status == BatchReservationStatus.UnresolvedAttempt) != (unresolvedAttemptId is not null)
            || (attempt is not null && (attempt.Envelope.BatchId != batch.Id || attempt.Envelope.Key != batch.Key))
            || (status == BatchReservationStatus.Reserved && (batch.Status != DeliveryBatchStatus.Open || !batch.FoldComplete))
            || (resolution is not null && (status != BatchReservationStatus.Replayed || attempt is null
                || resolution.AttemptId != attempt.AttemptId || resolution.BatchId != batch.Id))
            || (status == BatchReservationStatus.Closed && batch.Status != DeliveryBatchStatus.Closed))
            throw new ArgumentException("Reservation results must identify their actual immutable authority.");
        Batch = batch;
        Attempt = attempt;
        ReasonCode = BatchValidation.Code(reasonCode);
        Resolution = resolution;
    }
    public BatchReservationStatus Status { get; }
    public DeliveryBatchSnapshot Batch { get; }
    public BatchAttemptSnapshot? Attempt { get; }
    public Guid? UnresolvedAttemptId { get; }
    public string ReasonCode { get; }
    /// <summary>Resolved replay returns its original receipt; a replay without it requires recovery, never a blind resend.</summary>
    public BatchAttemptReceipt? Resolution { get; }
}

/// <summary>A single bounded audit-page item. It is never a caller-selected result write set.</summary>
public sealed record BatchAttemptCoverageItem
{
    public BatchAttemptCoverageItem(Guid eventId, long triggerOrdinal, long outboxRevisionAtSeal, int attemptsBefore)
    {
        EventId = PersistenceValidation.Id(eventId, nameof(eventId));
        TriggerOrdinal = PersistenceValidation.Positive(triggerOrdinal, nameof(triggerOrdinal));
        OutboxRevisionAtSeal = PersistenceValidation.Positive(outboxRevisionAtSeal, nameof(outboxRevisionAtSeal));
        ArgumentOutOfRangeException.ThrowIfNegative(attemptsBefore);
        AttemptsBefore = attemptsBefore;
    }
    public Guid EventId { get; }
    public long TriggerOrdinal { get; }
    public long OutboxRevisionAtSeal { get; }
    public int AttemptsBefore { get; }
}

/// <summary>
/// Additional capabilities implemented by the same watch store. SQL work finishes before OS awaits.
/// No method accepts arbitrary event-ID membership; claims and per-event classifications are store-derived.
/// </summary>
public interface IBatchDeliveryStore
{
    /// <summary>Bound limit to 1..MaxPageSize; order work by oldest pending trigger ordinal.</summary>
    Task<ImmutableArray<SectionDeliveryWork>> ReadPendingDeliverySectionsAsync(int limit, CancellationToken cancellationToken);
    /// <summary>One bounded fold page through a captured cutoff; an unresolved attempt forbids mutation.</summary>
    Task<BatchProjection> ProjectDeliveryBatchAsync(ProjectDeliveryBatchCommand command, CancellationToken cancellationToken);
    /// <summary>Stream one consistent transaction into bounded summaries using this exact policy and UTC.</summary>
    Task<BatchDeliveryContext> ReadDeliveryBatchContextAsync(Guid batchId, BatchDeliveryPolicy policy,
        DateTimeOffset evaluatedAtUtc, CancellationToken cancellationToken);
    /// <summary>
    /// Check request replay first; recompute counts/union/digests and expiry at transaction time, then CAS
    /// batch, section and catalog. Resolve nonsendable events internally or freeze one immutable attempt.
    /// Other planner dispositions cause no writes; a changed snapshot must be reread and prepared again.
    /// </summary>
    Task<BatchReservationResult> TryReserveBatchAttemptAsync(PreparedBatchAttempt command, CancellationToken cancellationToken);
    /// <summary>
    /// Attempt/result ledger is authority before mutable versions. Atomic coverage-wide result and histories;
    /// each covered event consumes its own budget for RetryableFailure/InterruptedUnknown, none for NotSubmitted.
    /// Identical replay returns the original receipt; conflicting facts explicitly fail. Known OS results must
    /// be recorded with uncancelled internal work. No section/catalog version can invalidate actual acceptance.
    /// </summary>
    Task<BatchAttemptReceipt> RecordBatchAttemptResultAsync(BatchAttemptResultCommand command, CancellationToken cancellationToken);
    /// <summary>Historical inspection only; positive bounded limit and exclusive nonnegative trigger cursor.</summary>
    Task<ImmutableArray<BatchAttemptCoverageItem>> ReadBatchAttemptCoverageAsync(Guid attemptId, long afterTriggerOrdinal,
        int limit, CancellationToken cancellationToken);
}
