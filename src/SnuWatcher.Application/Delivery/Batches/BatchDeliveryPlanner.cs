using System.Collections.Immutable;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Delivery.Batches;

public enum BatchPlanDisposition
{
    Send, NeedsFreshCheck, ResolveWithoutSend, NeedsRevalidation, MoreToFold, UnresolvedAttempt, Closed, RecipientLimitReached
}

/// <summary>Candidate content only; an immutable reserved attempt is required before any channel call.</summary>
public sealed record BatchDeliveryContent
{
    internal BatchDeliveryContent(SeatBaseline baseline, ImmutableArray<BatchRecipient> recipients, BatchCoverageSummary coverage)
    {
        Key = baseline.Key;
        CurrentCounts = baseline.Seats;
        CurrentSuccessAtUtc = baseline.ObservedAt.ToUniversalTime();
        CurrentSourceOrdinal = baseline.AttemptSequence;
        EligibleRecipients = recipients;
        Coverage = coverage;
    }
    public SectionKey Key { get; }
    public SeatCount CurrentCounts { get; }
    public DateTimeOffset CurrentSuccessAtUtc { get; }
    public long CurrentSourceOrdinal { get; }
    public ImmutableArray<BatchRecipient> EligibleRecipients { get; }
    public BatchCoverageSummary Coverage { get; }
}

public sealed record BatchDeliveryDecision
{
    internal BatchDeliveryDecision(BatchPlanDisposition disposition, string reasonCode, BatchDeliveryContent? content = null)
    {
        Disposition = disposition;
        ReasonCode = reasonCode;
        Content = content;
    }
    public BatchPlanDisposition Disposition { get; }
    public string ReasonCode { get; }
    public BatchDeliveryContent? Content { get; }

    internal bool SemanticallyEquals(BatchDeliveryDecision other) => Disposition == other.Disposition && ReasonCode == other.ReasonCode
        && (Content is null ? other.Content is null : other.Content is { } right && Content.Key == right.Key
            && Content.CurrentCounts == right.CurrentCounts && Content.CurrentSuccessAtUtc == right.CurrentSuccessAtUtc
            && Content.CurrentSourceOrdinal == right.CurrentSourceOrdinal && Content.Coverage == right.Coverage
            && BatchValidation.SameRecipients(Content.EligibleRecipients, right.EligibleRecipients));
}

/// <summary>No I/O or clock; aggregate event membership can only be recomputed by the consistent store read/seal.</summary>
public static class BatchDeliveryPlanner
{
    public static BatchDeliveryDecision Plan(BatchDeliveryContext context, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Batch.Status == DeliveryBatchStatus.Closed) return Decision(BatchPlanDisposition.Closed, "batch_closed");
        if (context.UnresolvedAttemptId is not null) return Decision(BatchPlanDisposition.UnresolvedAttempt, "attempt_unresolved");
        if (!context.Batch.FoldComplete) return Decision(BatchPlanDisposition.MoreToFold, "fold_incomplete");
        if (context.RecipientLimitExceeded) return Decision(BatchPlanDisposition.RecipientLimitReached, "recipient_limit");
        if (now < context.EvaluatedAtUtc) return Decision(BatchPlanDisposition.NeedsRevalidation, "clock_changed");
        if (context.ValidUntilUtc is { } boundary && now >= boundary)
            return Decision(BatchPlanDisposition.NeedsRevalidation, "eligibility_expired");
        if (context.EligibleCoverage.EventCount == 0) return Decision(BatchPlanDisposition.ResolveWithoutSend, "no_eligible_events");
        if (!RecipientsStillEligible(context, now)) return Decision(BatchPlanDisposition.NeedsRevalidation, "recipient_state_changed");
        if (context.Section.Baseline is not { } current) return Decision(BatchPlanDisposition.NeedsFreshCheck, "missing_current_counts");
        if (context.Section.LatestOutcome != CheckOutcome.Success) return Decision(BatchPlanDisposition.NeedsFreshCheck, "latest_check_unavailable");
        if (current.AttemptSequence < context.EligibleCoverage.MaxTriggerOrdinal) return Decision(BatchPlanDisposition.NeedsFreshCheck, "source_before_trigger");
        if (current.ObservedAt > now) return Decision(BatchPlanDisposition.NeedsFreshCheck, "clock_changed");
        if (current.Seats.ApparentSeats == 0) return Decision(BatchPlanDisposition.ResolveWithoutSend, "section_full");
        if (now - current.ObservedAt > context.Policy.MaxAge) return Decision(BatchPlanDisposition.NeedsFreshCheck, "source_stale");
        return new(BatchPlanDisposition.Send, "ready", new(current, context.EligibleRecipients, context.EligibleCoverage));
    }

    private static bool RecipientsStillEligible(BatchDeliveryContext context, DateTimeOffset now)
    {
        var subscriptions = context.Subscriptions.ToDictionary(s => s.Id);
        return context.EligibleRecipients.All(recipient => subscriptions.TryGetValue(recipient.SubscriptionId, out var watch)
            && watch.Status == WatchStatus.Active && watch.ActivationGeneration == recipient.Generation
            && watch.Members.Any(member => member.Key == context.Batch.Key) && (watch.Expiry is null || !watch.Expiry.IsExpiredAt(now)));
    }

    private static BatchDeliveryDecision Decision(BatchPlanDisposition disposition, string reasonCode) => new(disposition, reasonCode);
}

/// <summary>
/// Caller-stable reservation identity plus canonical snapshot/effects. Store must recompute facts and
/// eligibility at its transaction time, CAS all three versions, and check request replay first.
/// ImmutableArray record equality is deliberately not used to validate the proposed decision.
/// </summary>
public sealed record PreparedBatchAttempt
{
    public PreparedBatchAttempt(Guid reservationRequestId, BatchDeliveryContext context, BatchDeliveryDecision decision, DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(decision);
        ReservationRequestId = PersistenceValidation.Id(reservationRequestId, nameof(reservationRequestId));
        if (!decision.SemanticallyEquals(BatchDeliveryPlanner.Plan(context, evaluatedAtUtc)))
            throw new ArgumentException("Prepared effects must match the current immutable batch context.", nameof(decision));
        Context = context;
        Decision = decision;
        EvaluatedAtUtc = evaluatedAtUtc.ToUniversalTime();
    }
    public Guid ReservationRequestId { get; }
    public BatchDeliveryContext Context { get; }
    public BatchDeliveryDecision Decision { get; }
    public DateTimeOffset EvaluatedAtUtc { get; }
}
