using System.Collections.Immutable;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Delivery.Batches;

public static class DeliveryRecoveryLimits
{
    public const int MaxPageSize = 1000;
}

/// <summary>Unresolved reservation identity only; it is never permission to submit its saved envelope.</summary>
public sealed record UnresolvedDeliveryAttempt
{
    public UnresolvedDeliveryAttempt(Guid attemptId, Guid batchId, SectionKey key, DateTimeOffset preparedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(key);
        AttemptId = PersistenceValidation.Id(attemptId, nameof(attemptId));
        BatchId = PersistenceValidation.Id(batchId, nameof(batchId));
        Key = key;
        PreparedAtUtc = preparedAtUtc.ToUniversalTime();
    }
    public Guid AttemptId { get; }
    public Guid BatchId { get; }
    public SectionKey Key { get; }
    public DateTimeOffset PreparedAtUtc { get; }
}

/// <summary>Startup discovery independent of ordinary pending section/event work.</summary>
public interface IDeliveryRecoveryStore
{
    /// <summary>
    /// Query only unresolved attempts, bounded to 1..1000 rows. Order by canonical lowercase GuidN
    /// using ordinal/binary comparison and an exclusive cursor. A null cursor begins discovery.
    /// Own resolutions remove earlier rows, so offset paging must never be used.
    /// </summary>
    Task<ImmutableArray<UnresolvedDeliveryAttempt>> ReadUnresolvedAttemptsAsync(Guid? afterAttemptId,
        int limit, CancellationToken cancellationToken);
}
