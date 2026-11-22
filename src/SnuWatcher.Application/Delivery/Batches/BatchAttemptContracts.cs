using System.Collections.Immutable;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Delivery.Batches;

/// <summary>
/// Frozen submission content. Its batch key persists across attempts; current count facts and eligible
/// coverage remain distinct from immutable original triggers stored relationally. No arbitrary URI is accepted.
/// </summary>
public sealed record BatchDeliveryEnvelope
{
    public BatchDeliveryEnvelope(Guid batchId, Guid attemptId, SectionKey key, SeatCount currentCounts,
        DateTimeOffset currentSuccessAtUtc, long currentSourceOrdinal, IEnumerable<BatchRecipient> eligibleRecipients,
        BatchCoverageSummary coverage)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(coverage);
        BatchId = PersistenceValidation.Id(batchId, nameof(batchId));
        AttemptId = PersistenceValidation.Id(attemptId, nameof(attemptId));
        CurrentSourceOrdinal = PersistenceValidation.Positive(currentSourceOrdinal, nameof(currentSourceOrdinal));
        EligibleRecipients = BatchValidation.Recipients(eligibleRecipients);
        if (currentCounts.ApparentSeats == 0 || EligibleRecipients.IsEmpty || coverage.EventCount == 0
            || currentSourceOrdinal < coverage.MaxTriggerOrdinal)
            throw new ArgumentException("A submission needs open current counts, complete eligible coverage and recipients.");
        Key = key;
        CurrentCounts = currentCounts;
        CurrentSuccessAtUtc = currentSuccessAtUtc.ToUniversalTime();
        Coverage = coverage;
    }
    public Guid BatchId { get; }
    public string NativeDedupeKey => BatchId.ToString("N");
    public Guid AttemptId { get; }
    public SectionKey Key { get; }
    public SeatCount CurrentCounts { get; }
    public int CurrentSeats => CurrentCounts.ApparentSeats;
    public DateTimeOffset CurrentSuccessAtUtc { get; }
    public long CurrentSourceOrdinal { get; }
    public ImmutableArray<BatchRecipient> EligibleRecipients { get; }
    public BatchCoverageSummary Coverage { get; }
}

/// <summary>Immutable reservation authority; it alone grants membership for recording one actual result.</summary>
public sealed record BatchAttemptSnapshot
{
    public BatchAttemptSnapshot(Guid attemptId, Guid reservationRequestId, long attemptSequence, DateTimeOffset preparedAtUtc,
        BatchDeliveryPolicy policy, BatchDeliveryEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(envelope);
        AttemptId = PersistenceValidation.Id(attemptId, nameof(attemptId));
        ReservationRequestId = PersistenceValidation.Id(reservationRequestId, nameof(reservationRequestId));
        AttemptSequence = PersistenceValidation.Positive(attemptSequence, nameof(attemptSequence));
        if (envelope.AttemptId != attemptId) throw new ArgumentException("Envelope must identify this exact reservation.", nameof(envelope));
        PreparedAtUtc = preparedAtUtc.ToUniversalTime();
        Policy = policy;
        Envelope = envelope;
    }
    public Guid AttemptId { get; }
    public Guid ReservationRequestId { get; }
    public long AttemptSequence { get; }
    public DateTimeOffset PreparedAtUtc { get; }
    public BatchDeliveryPolicy Policy { get; }
    public BatchDeliveryEnvelope Envelope { get; }
}

public enum BatchAttemptResultOutcome { InterruptedUnknown = 0, OsAccepted = 1, RetryableFailure = 2, NotSubmitted = 3 }

/// <summary>
/// Result facts only. It cannot supply membership, statuses or counters. NotSubmitted costs no attempt;
/// InterruptedUnknown conservatively consumes one unit per frozen member without implying acceptance.
/// </summary>
public sealed record BatchAttemptResultCommand
{
    public BatchAttemptResultCommand(Guid attemptId, BatchAttemptResultOutcome outcome, string diagnosticCode, DateTimeOffset recordedAtUtc)
    {
        AttemptId = PersistenceValidation.Id(attemptId, nameof(attemptId));
        Outcome = PersistenceValidation.Defined(outcome, nameof(outcome));
        DiagnosticCode = BatchValidation.Code(diagnosticCode);
        RecordedAtUtc = recordedAtUtc.ToUniversalTime();
    }
    public Guid AttemptId { get; }
    public BatchAttemptResultOutcome Outcome { get; }
    public string DiagnosticCode { get; }
    public DateTimeOffset RecordedAtUtc { get; }
}

/// <summary>Store-derived per-member resolution counts; identical ledger replay returns this original receipt.</summary>
public sealed record BatchAttemptReceipt
{
    public BatchAttemptReceipt(Guid attemptId, Guid batchId, BatchAttemptResultOutcome outcome, long coveredEventCount,
        long acceptedCount, long pendingCount, long failedCount, DateTimeOffset recordedAtUtc)
    {
        AttemptId = PersistenceValidation.Id(attemptId, nameof(attemptId));
        BatchId = PersistenceValidation.Id(batchId, nameof(batchId));
        Outcome = PersistenceValidation.Defined(outcome, nameof(outcome));
        CoveredEventCount = PersistenceValidation.Positive(coveredEventCount, nameof(coveredEventCount));
        AcceptedCount = PersistenceValidation.Nonnegative(acceptedCount, nameof(acceptedCount));
        PendingCount = PersistenceValidation.Nonnegative(pendingCount, nameof(pendingCount));
        FailedCount = PersistenceValidation.Nonnegative(failedCount, nameof(failedCount));
        if (acceptedCount > coveredEventCount || pendingCount > coveredEventCount - acceptedCount
            || failedCount != coveredEventCount - acceptedCount - pendingCount
            || (outcome == BatchAttemptResultOutcome.OsAccepted ? acceptedCount != coveredEventCount : acceptedCount != 0)
            || (outcome == BatchAttemptResultOutcome.NotSubmitted && pendingCount != coveredEventCount))
            throw new ArgumentException("Receipt counts must agree with the authoritative outcome and frozen coverage.");
        RecordedAtUtc = recordedAtUtc.ToUniversalTime();
    }
    public Guid AttemptId { get; }
    public Guid BatchId { get; }
    public BatchAttemptResultOutcome Outcome { get; }
    public long CoveredEventCount { get; }
    public long AcceptedCount { get; }
    public long PendingCount { get; }
    public long FailedCount { get; }
    public DateTimeOffset RecordedAtUtc { get; }
}

/// <summary>
/// Readiness never submits. No default-Ready implementation is provided. Send must finish the actual
/// OS operation before completing, including a retained synchronous platform call that cannot be aborted.
/// Never return task cancellation while submission continues outside the lifecycle gate. Known acceptance
/// survives late cancellation; NotSubmitted proves that no OS submission began. The driver rechecks the
/// transient validity window immediately before that boundary. No synthetic single-event trigger is used.
/// </summary>
public interface IBatchDesktopAlertChannel
{
    Task<BatchChannelReadiness> ReadReadinessAsync(CancellationToken cancellationToken);
    Task<BatchChannelResult> SendAsync(BatchDesktopSubmission submission, CancellationToken cancellationToken);
}
