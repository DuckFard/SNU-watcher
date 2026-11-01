using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Delivery.Batches;

public enum DeliveryBatchStatus { Open, Closed }
public enum BatchProjectionStatus { NoWork, MoreToFold, ReadyToEvaluate, UnresolvedAttempt }

public sealed record SectionDeliveryWork
{
    public SectionDeliveryWork(SectionKey key, long oldestPendingOrdinal, Guid? openBatchId, bool hasUnresolvedAttempt)
    {
        ArgumentNullException.ThrowIfNull(key);
        Key = key;
        OldestPendingOrdinal = PersistenceValidation.Positive(oldestPendingOrdinal, nameof(oldestPendingOrdinal));
        OpenBatchId = BatchValidation.OptionalId(openBatchId, nameof(openBatchId));
        if (hasUnresolvedAttempt && openBatchId is null) throw new ArgumentException("An unfinished attempt needs its open batch.");
        HasUnresolvedAttempt = hasUnresolvedAttempt;
    }
    public SectionKey Key { get; }
    public long OldestPendingOrdinal { get; }
    public Guid? OpenBatchId { get; }
    public bool HasUnresolvedAttempt { get; }
}

public sealed record ProjectDeliveryBatchCommand
{
    public ProjectDeliveryBatchCommand(SectionKey key, int pageSize, DateTimeOffset projectedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (pageSize is < 1 or > BatchDeliveryLimits.MaxPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize));
        Key = key;
        PageSize = pageSize;
        ProjectedAtUtc = projectedAtUtc.ToUniversalTime();
    }
    public SectionKey Key { get; }
    public int PageSize { get; }
    public DateTimeOffset ProjectedAtUtc { get; }
}

/// <summary>Fold cursor and cutoff use persisted trigger order, never wall-clock time.</summary>
public sealed record DeliveryBatchSnapshot
{
    public DeliveryBatchSnapshot(Guid id, SectionKey key, long revision, DeliveryBatchStatus status,
        long foldCutoffOrdinal, long foldCursorOrdinal, bool foldComplete, DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(key);
        Id = PersistenceValidation.Id(id, nameof(id));
        Key = key;
        Revision = PersistenceValidation.Positive(revision, nameof(revision));
        Status = PersistenceValidation.Defined(status, nameof(status));
        FoldCutoffOrdinal = PersistenceValidation.Nonnegative(foldCutoffOrdinal, nameof(foldCutoffOrdinal));
        FoldCursorOrdinal = PersistenceValidation.Nonnegative(foldCursorOrdinal, nameof(foldCursorOrdinal));
        if (foldCursorOrdinal > foldCutoffOrdinal || foldComplete != (foldCursorOrdinal == foldCutoffOrdinal))
            throw new ArgumentException("Fold completion must identify the captured cutoff.");
        FoldComplete = foldComplete;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
    }
    public Guid Id { get; }
    public SectionKey Key { get; }
    public long Revision { get; }
    public DeliveryBatchStatus Status { get; }
    public long FoldCutoffOrdinal { get; }
    public long FoldCursorOrdinal { get; }
    public bool FoldComplete { get; }
    public DateTimeOffset CreatedAtUtc { get; }
}

public sealed record BatchProjection
{
    public BatchProjection(BatchProjectionStatus status, DeliveryBatchSnapshot? batch, Guid? unresolvedAttemptId)
    {
        Status = PersistenceValidation.Defined(status, nameof(status));
        UnresolvedAttemptId = BatchValidation.OptionalId(unresolvedAttemptId, nameof(unresolvedAttemptId));
        var valid = status switch
        {
            BatchProjectionStatus.NoWork => batch is null && unresolvedAttemptId is null,
            BatchProjectionStatus.MoreToFold => batch is { Status: DeliveryBatchStatus.Open, FoldComplete: false } && unresolvedAttemptId is null,
            BatchProjectionStatus.ReadyToEvaluate => batch is { Status: DeliveryBatchStatus.Open, FoldComplete: true } && unresolvedAttemptId is null,
            _ => batch is { Status: DeliveryBatchStatus.Open } && unresolvedAttemptId is not null,
        };
        if (!valid) throw new ArgumentException("Projection status must identify its actual fold or unresolved authority.");
        Batch = batch;
    }
    public BatchProjectionStatus Status { get; }
    public DeliveryBatchSnapshot? Batch { get; }
    public Guid? UnresolvedAttemptId { get; }
}

/// <summary>
/// Bounded evidence of relational coverage. The versioned store digest binds event IDs, current outbox
/// revisions, attempts-before, trigger order and eligible reason associations in canonical order.
/// Exact covered IDs remain relational and are inspected through bounded audit pages.
/// </summary>
public sealed record BatchCoverageSummary
{
    public BatchCoverageSummary(long eventCount, string digest, long minTriggerOrdinal, long maxTriggerOrdinal)
    {
        EventCount = PersistenceValidation.Nonnegative(eventCount, nameof(eventCount));
        Digest = BatchValidation.Digest(digest);
        MinTriggerOrdinal = PersistenceValidation.Nonnegative(minTriggerOrdinal, nameof(minTriggerOrdinal));
        MaxTriggerOrdinal = PersistenceValidation.Nonnegative(maxTriggerOrdinal, nameof(maxTriggerOrdinal));
        if (eventCount == 0 ? minTriggerOrdinal != 0 || maxTriggerOrdinal != 0
            : minTriggerOrdinal == 0 || maxTriggerOrdinal < minTriggerOrdinal
                || eventCount > maxTriggerOrdinal - minTriggerOrdinal + 1 || (eventCount == 1 && minTriggerOrdinal != maxTriggerOrdinal))
            throw new ArgumentException("Coverage count and unique trigger range must agree.");
    }
    public long EventCount { get; }
    public string Digest { get; }
    public long MinTriggerOrdinal { get; }
    public long MaxTriggerOrdinal { get; }
}
