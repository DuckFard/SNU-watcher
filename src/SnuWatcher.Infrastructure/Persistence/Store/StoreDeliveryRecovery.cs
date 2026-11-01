using System.Collections.Immutable;
using SnuWatcher.Application.Delivery.Batches;

namespace SnuWatcher.Infrastructure.Persistence;

public sealed partial class SqliteWatchStore : IDeliveryRecoveryStore
{
    public Task<ImmutableArray<UnresolvedDeliveryAttempt>> ReadUnresolvedAttemptsAsync(Guid? afterAttemptId, int limit,
        CancellationToken cancellationToken = default)
    {
        if (afterAttemptId == Guid.Empty) throw new ArgumentException("A recovery cursor must identify an actual attempt.", nameof(afterAttemptId));
        if (limit is < 1 or > DeliveryRecoveryLimits.MaxPageSize) throw new ArgumentOutOfRangeException(nameof(limit));
        return _lane.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            List<UnresolvedDeliveryAttempt> rows = [];
            using (var command = Command(connection, transaction, """
                SELECT a.id,a.batchId,b.sectionKey,a.snapshot,b.status
                FROM DeliveryBatchAttempts a LEFT JOIN DeliveryBatches b ON b.id=a.batchId
                WHERE a.status=0 AND a.id COLLATE BINARY>$after
                ORDER BY a.id COLLATE BINARY LIMIT $limit;
                """, ("$after", afterAttemptId?.ToString("N") ?? ""), ("$limit", limit)))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    if (reader.IsDBNull(2) || reader.GetInt32(4) != (int)DeliveryBatchStatus.Open)
                        throw new InvalidDataException("An unresolved reservation has no open batch authority.");
                    var attemptId = Guid.ParseExact(reader.GetString(0), "N");
                    var batchId = Guid.ParseExact(reader.GetString(1), "N");
                    var key = StoreWire.ReadKey(reader.GetString(2));
                    var attempt = StoreBatchWire.ReadAttempt(reader.GetString(3));
                    if (attemptId.ToString("N") != reader.GetString(0) || batchId.ToString("N") != reader.GetString(1)
                        || attempt.AttemptId != attemptId || attempt.Envelope.BatchId != batchId || attempt.Envelope.Key != key)
                        throw new InvalidDataException("Recovery identity must match its immutable reserved facts.");
                    rows.Add(new(attemptId, batchId, key, attempt.PreparedAtUtc));
                }
            transaction.Commit();
            return rows.ToImmutableArray();
        }, cancellationToken);
    }
}
