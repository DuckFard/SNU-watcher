using System.Collections.Immutable;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Delivery.Batches;

/// <summary>Exclusive live-work position, ordered by oldest pending ordinal and exact stored full key.</summary>
public sealed record PendingDeliveryWorkCursor
{
    public PendingDeliveryWorkCursor(long oldestPendingOrdinal, SectionKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        OldestPendingOrdinal = PersistenceValidation.Positive(oldestPendingOrdinal, nameof(oldestPendingOrdinal));
        Key = key;
    }
    public long OldestPendingOrdinal { get; }
    public SectionKey Key { get; }
}

/// <summary>
/// One bounded page, not a cross-page snapshot. Concurrent changes may move work; restarting from null
/// discovers new work behind the cursor. NextCursor exists only when another page was observed.
/// </summary>
public sealed record PendingDeliveryWorkPage
{
    public PendingDeliveryWorkPage(IEnumerable<SectionDeliveryWork> items, PendingDeliveryWorkCursor? nextCursor, bool hasMore)
    {
        Items = PersistenceValidation.Snapshot(items, BatchDeliveryLimits.MaxPageSize, nameof(items));
        if (Items.DistinctBy(item => item.Key).Count() != Items.Length || hasMore != (nextCursor is not null)
            || (hasMore && (Items.IsEmpty || nextCursor!.Key != Items[^1].Key
                || nextCursor.OldestPendingOrdinal != Items[^1].OldestPendingOrdinal)))
            throw new ArgumentException("A page requires unique sections and its actual exclusive last-item cursor.");
        NextCursor = nextCursor;
        HasMore = hasMore;
    }
    public ImmutableArray<SectionDeliveryWork> Items { get; }
    public PendingDeliveryWorkCursor? NextCursor { get; }
    public bool HasMore { get; }
}

/// <summary>Separate capability; it does not change the earlier first-page compatibility port.</summary>
public interface IPendingDeliveryWorkReader
{
    Task<PendingDeliveryWorkPage> ReadPageAsync(PendingDeliveryWorkCursor? after, int limit, CancellationToken cancellationToken);
}
