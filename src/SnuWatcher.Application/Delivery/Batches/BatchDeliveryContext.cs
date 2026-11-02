using System.Collections.Immutable;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Delivery.Batches;

public sealed record BatchDeliveryPolicy
{
    public BatchDeliveryPolicy(TimeSpan maxAge, int maxAttempts)
    {
        if (maxAge <= TimeSpan.Zero || maxAge > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(maxAge));
        if (maxAttempts is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        MaxAge = maxAge;
        MaxAttempts = maxAttempts;
    }
    public TimeSpan MaxAge { get; }
    public int MaxAttempts { get; }
}

/// <summary>Original reason set for one current subscription generation; references stay relational.</summary>
public sealed record BatchRecipient
{
    public BatchRecipient(Guid subscriptionId, long generation, IEnumerable<OpeningReason> reasons)
    {
        SubscriptionId = PersistenceValidation.Id(subscriptionId, nameof(subscriptionId));
        Generation = PersistenceValidation.Positive(generation, nameof(generation));
        ArgumentNullException.ThrowIfNull(reasons);
        if (reasons is ImmutableArray<OpeningReason> { IsDefault: true }) throw new ArgumentException("Reasons must be initialized.", nameof(reasons));
        var snapshot = reasons.Take(3).ToImmutableArray();
        if (snapshot.Length is < 1 or > 2) throw new ArgumentException("A bounded nonempty original reason set is required.", nameof(reasons));
        foreach (var reason in snapshot) PersistenceValidation.Defined(reason, nameof(reasons));
        Reasons = snapshot.Distinct().Order().ToImmutableArray();
    }
    public Guid SubscriptionId { get; }
    public long Generation { get; }
    public ImmutableArray<OpeningReason> Reasons { get; }
}

/// <summary>
/// One consistent streamed store reduction at the supplied time and policy. Eligible coverage contains
/// only sendable pending events; ineligible and exhausted counts are disjoint and resolved by the store.
/// The bounded union cannot recompute per-event overlap after an expiry, so the planner requests a reread.
/// An explicit overflow blocks submission; materialization limits never silently acknowledge omitted events.
/// </summary>
public sealed record BatchDeliveryContext
{
    public BatchDeliveryContext(DeliveryBatchSnapshot batch, SectionSnapshot section, long catalogRevision,
        BatchDeliveryPolicy policy, DateTimeOffset evaluatedAtUtc, DateTimeOffset? validUntilUtc,
        long pendingCount, long ineligibleCount, long budgetExhaustedCount, BatchCoverageSummary eligibleCoverage,
        string allFactsDigest, IEnumerable<BatchRecipient> eligibleRecipients, IEnumerable<SubscriptionSnapshot> subscriptions,
        Guid? unresolvedAttemptId, bool recipientLimitExceeded = false)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(eligibleCoverage);
        if (batch.Key != section.Key) throw new ArgumentException("Batch and section must identify one full section.");
        Batch = batch;
        Section = section;
        CatalogRevision = PersistenceValidation.Nonnegative(catalogRevision, nameof(catalogRevision));
        Policy = policy;
        EvaluatedAtUtc = evaluatedAtUtc.ToUniversalTime();
        ValidUntilUtc = validUntilUtc?.ToUniversalTime();
        PendingCount = PersistenceValidation.Nonnegative(pendingCount, nameof(pendingCount));
        IneligibleCount = PersistenceValidation.Nonnegative(ineligibleCount, nameof(ineligibleCount));
        BudgetExhaustedCount = PersistenceValidation.Nonnegative(budgetExhaustedCount, nameof(budgetExhaustedCount));
        if (ineligibleCount > pendingCount || budgetExhaustedCount > pendingCount - ineligibleCount
            || eligibleCoverage.EventCount != pendingCount - ineligibleCount - budgetExhaustedCount)
            throw new ArgumentException("Pending events must partition into eligible, ineligible and exhausted counts.");
        if (eligibleCoverage.MaxTriggerOrdinal > batch.FoldCutoffOrdinal)
            throw new ArgumentException("Eligible coverage cannot exceed the captured fold cutoff.");
        EligibleCoverage = eligibleCoverage;
        AllFactsDigest = BatchValidation.Digest(allFactsDigest);
        EligibleRecipients = BatchValidation.Recipients(eligibleRecipients);
        Subscriptions = PersistenceValidation.Snapshot(subscriptions, BatchDeliveryLimits.MaxRecipients, nameof(subscriptions));
        if (Subscriptions.DistinctBy(s => s.Id).Count() != Subscriptions.Length) throw new ArgumentException("Current subscriptions must be unique.");
        if (recipientLimitExceeded ? !EligibleRecipients.IsEmpty || !Subscriptions.IsEmpty
            : (eligibleCoverage.EventCount == 0) != EligibleRecipients.IsEmpty)
            throw new ArgumentException("Eligible event coverage needs a complete recipient union or an explicit empty overflow result.");
        if (ValidUntilUtc is { } boundary && (boundary <= EvaluatedAtUtc || eligibleCoverage.EventCount == 0))
            throw new ArgumentException("An eligibility boundary must be after aggregation and have eligible events.");
        UnresolvedAttemptId = BatchValidation.OptionalId(unresolvedAttemptId, nameof(unresolvedAttemptId));
        if (batch.Status == DeliveryBatchStatus.Closed && (pendingCount != 0 || unresolvedAttemptId is not null))
            throw new ArgumentException("A closed batch has no pending members or unfinished authority.");
        RecipientLimitExceeded = recipientLimitExceeded;
    }
    public DeliveryBatchSnapshot Batch { get; }
    public SectionSnapshot Section { get; }
    public long CatalogRevision { get; }
    public BatchDeliveryPolicy Policy { get; }
    public DateTimeOffset EvaluatedAtUtc { get; }
    public DateTimeOffset? ValidUntilUtc { get; }
    public long PendingCount { get; }
    public long IneligibleCount { get; }
    public long BudgetExhaustedCount { get; }
    public BatchCoverageSummary EligibleCoverage { get; }
    public string AllFactsDigest { get; }
    public ImmutableArray<BatchRecipient> EligibleRecipients { get; }
    public ImmutableArray<SubscriptionSnapshot> Subscriptions { get; }
    public Guid? UnresolvedAttemptId { get; }
    public bool RecipientLimitExceeded { get; }
}
