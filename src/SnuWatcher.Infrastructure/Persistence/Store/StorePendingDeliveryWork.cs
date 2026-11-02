using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Persistence;

namespace SnuWatcher.Infrastructure.Persistence;

public sealed partial class SqliteWatchStore : IPendingDeliveryWorkReader
{
    public Task<PendingDeliveryWorkPage> ReadPageAsync(PendingDeliveryWorkCursor? after, int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > BatchDeliveryLimits.MaxPageSize) throw new ArgumentOutOfRangeException(nameof(limit));
        return _lane.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            List<SectionDeliveryWork> items = [];
            using (var command = Command(connection, transaction, """
                SELECT e.sectionKey,min(e.triggerOrdinal),b.id,
                    EXISTS(SELECT 1 FROM DeliveryBatchAttempts a WHERE a.batchId=b.id AND a.status=0)
                FROM Events e JOIN Outbox o ON o.eventId=e.id
                LEFT JOIN DeliveryBatches b ON b.sectionKey=e.sectionKey AND b.status=0
                WHERE o.status=$pending GROUP BY e.sectionKey,b.id
                HAVING min(e.triggerOrdinal)>$afterOrdinal OR
                    (min(e.triggerOrdinal)=$afterOrdinal AND e.sectionKey COLLATE BINARY > $afterKey COLLATE BINARY)
                ORDER BY min(e.triggerOrdinal),e.sectionKey COLLATE BINARY LIMIT $limit;
                """, ("$pending", (int)DeliveryStatus.Pending), ("$afterOrdinal", after?.OldestPendingOrdinal ?? 0),
                    ("$afterKey", after is null ? "" : StoreWire.Key(after.Key)), ("$limit", limit + 1)))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) items.Add(new(StoreWire.ReadKey(reader.GetString(0)), reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : Guid.ParseExact(reader.GetString(2), "N"), reader.GetBoolean(3)));
            transaction.Commit();
            var more = items.Count > limit;
            var page = items.Take(limit).ToArray();
            var cursor = more ? new PendingDeliveryWorkCursor(page[^1].OldestPendingOrdinal, page[^1].Key) : null;
            return new PendingDeliveryWorkPage(page, cursor, more);
        }, cancellationToken);
    }
}
