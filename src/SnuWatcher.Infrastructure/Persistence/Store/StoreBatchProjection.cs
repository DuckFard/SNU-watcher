using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Infrastructure.Persistence;

public sealed partial class SqliteWatchStore
{
    public Task<ImmutableArray<SectionDeliveryWork>> ReadPendingDeliverySectionsAsync(int limit, CancellationToken cancellationToken = default)
    {
        BatchPageLimit(limit);
        return _lane.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            List<SectionDeliveryWork> result = [];
            using var command = Command(connection, transaction, """
                SELECT e.sectionKey,min(e.triggerOrdinal),b.id,
                EXISTS(SELECT 1 FROM DeliveryBatchAttempts a WHERE a.batchId=b.id AND a.status=0)
                FROM Events e JOIN Outbox o ON o.eventId=e.id
                LEFT JOIN DeliveryBatches b ON b.sectionKey=e.sectionKey AND b.status=0
                WHERE o.status=$pending GROUP BY e.sectionKey,b.id ORDER BY min(e.triggerOrdinal),e.sectionKey LIMIT $limit;
                """, ("$pending", (int)DeliveryStatus.Pending), ("$limit", limit));
            using (var reader = command.ExecuteReader())
                while (reader.Read()) result.Add(new(StoreWire.ReadKey(reader.GetString(0)), reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : Guid.ParseExact(reader.GetString(2), "N"), reader.GetBoolean(3)));
            transaction.Commit();
            return result.ToImmutableArray();
        }, cancellationToken);
    }

    public Task<BatchProjection> ProjectDeliveryBatchAsync(ProjectDeliveryBatchCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _lane.ExecuteAsync(connection =>
        {
            using var transaction = BeginWrite(connection);
            var section = StoreWire.Key(command.Key);
            var batch = FindOpenBatch(connection, transaction, command.Key);
            var unresolved = batch is null ? null : UnresolvedBatchAttempt(connection, transaction, batch.Id);
            if (unresolved is not null) return new BatchProjection(BatchProjectionStatus.UnresolvedAttempt, batch, unresolved);
            var newest = (long?)Scalar(connection, transaction, """
                SELECT max(e.triggerOrdinal) FROM Events e JOIN Outbox o ON o.eventId=e.id
                WHERE e.sectionKey=$section AND o.status=$pending AND NOT EXISTS(SELECT 1 FROM DeliveryBatchEvents m WHERE m.eventId=e.id);
                """, ("$section", section), ("$pending", (int)DeliveryStatus.Pending)) ?? 0;
            if (batch is null)
            {
                if (newest == 0) return new BatchProjection(BatchProjectionStatus.NoWork, null, null);
                batch = new(Guid.NewGuid(), command.Key, 1, DeliveryBatchStatus.Open, newest, 0, false, _time.GetUtcNow());
                Run(connection, transaction, "INSERT INTO DeliveryBatches(id,sectionKey,revision,status,foldCutoff,foldCursor,foldComplete,createdUtc) VALUES($id,$section,1,0,$cutoff,0,0,$at);",
                    ("$id", batch.Id.ToString("N")), ("$section", section), ("$cutoff", newest), ("$at", StoreWire.At(batch.CreatedAtUtc)));
            }
            else if (batch.FoldComplete)
            {
                // Each resolved attempt permits exactly one new fold cycle. Repeated projection
                // before its next seal cannot continuously move the already captured boundary.
                var resolved = (long?)Scalar(connection, transaction, "SELECT max(sequence) FROM DeliveryBatchAttempts WHERE batchId=$id AND status=1;",
                    ("$id", batch.Id.ToString("N"))) ?? 0;
                var foldedAfter = (long)Scalar(connection, transaction, "SELECT foldAfterAttempt FROM DeliveryBatches WHERE id=$id;", ("$id", batch.Id.ToString("N")))!;
                if (resolved > foldedAfter)
                {
                    var cutoff = Math.Max(newest, batch.FoldCutoffOrdinal);
                    batch = new(batch.Id, batch.Key, checked(batch.Revision + 1), batch.Status, cutoff,
                        batch.FoldCursorOrdinal, cutoff == batch.FoldCursorOrdinal, batch.CreatedAtUtc);
                    Run(connection, transaction, "UPDATE DeliveryBatches SET foldAfterAttempt=$sequence WHERE id=$id;",
                        ("$sequence", resolved), ("$id", batch.Id.ToString("N")));
                    UpdateBatch(connection, transaction, batch);
                    Checkpoint(SqliteStoreWriteStage.BatchProjected);
                }
            }

            if (batch.FoldComplete)
            {
                transaction.Commit();
                return new BatchProjection(BatchProjectionStatus.ReadyToEvaluate, batch, null);
            }
            List<(string Id, long Revision, long Ordinal)> page = [];
            using (var query = Command(connection, transaction, """
                SELECT e.id,o.revision,e.triggerOrdinal FROM Events e JOIN Outbox o ON o.eventId=e.id
                WHERE e.sectionKey=$section AND o.status=$pending AND e.triggerOrdinal>$cursor AND e.triggerOrdinal<=$cutoff
                AND NOT EXISTS(SELECT 1 FROM DeliveryBatchEvents m WHERE m.eventId=e.id) ORDER BY e.triggerOrdinal LIMIT $limit;
                """, ("$section", section), ("$pending", (int)DeliveryStatus.Pending), ("$cursor", batch.FoldCursorOrdinal),
                ("$cutoff", batch.FoldCutoffOrdinal), ("$limit", command.PageSize)))
            using (var reader = query.ExecuteReader()) while (reader.Read()) page.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
            var revision = checked(batch.Revision + 1);
            foreach (var row in page)
                Run(connection, transaction, "INSERT INTO DeliveryBatchEvents(batchId,eventId,claimedRevision,joinedRevision) VALUES($batch,$event,$revision,$joined);",
                    ("$batch", batch.Id.ToString("N")), ("$event", row.Id), ("$revision", row.Revision), ("$joined", revision));
            var cursor = page.Count == 0 ? batch.FoldCutoffOrdinal : page[^1].Ordinal;
            var more = (long)Scalar(connection, transaction, """
                SELECT EXISTS(SELECT 1 FROM Events e JOIN Outbox o ON o.eventId=e.id
                WHERE e.sectionKey=$section AND o.status=$pending AND e.triggerOrdinal>$cursor AND e.triggerOrdinal<=$cutoff
                AND NOT EXISTS(SELECT 1 FROM DeliveryBatchEvents m WHERE m.eventId=e.id));
                """, ("$section", section), ("$pending", (int)DeliveryStatus.Pending), ("$cursor", cursor), ("$cutoff", batch.FoldCutoffOrdinal))! != 0;
            if (!more) cursor = batch.FoldCutoffOrdinal;
            batch = new(batch.Id, batch.Key, revision, batch.Status, batch.FoldCutoffOrdinal, cursor, !more, batch.CreatedAtUtc);
            UpdateBatch(connection, transaction, batch);
            Checkpoint(SqliteStoreWriteStage.BatchProjected);
            transaction.Commit();
            return new BatchProjection(more ? BatchProjectionStatus.MoreToFold : BatchProjectionStatus.ReadyToEvaluate, batch, null);
        }, cancellationToken);
    }

    public Task<ImmutableArray<BatchAttemptCoverageItem>> ReadBatchAttemptCoverageAsync(Guid attemptId, long afterTriggerOrdinal,
        int limit, CancellationToken cancellationToken = default)
    {
        BatchId(attemptId); BatchPageLimit(limit);
        if (afterTriggerOrdinal < 0) throw new ArgumentOutOfRangeException(nameof(afterTriggerOrdinal));
        return _lane.ExecuteAsync(connection =>
        {
            List<BatchAttemptCoverageItem> result = [];
            using var query = Command(connection, null, "SELECT eventId,triggerOrdinal,expectedRevision,attemptsBefore FROM DeliveryBatchAttemptEvents WHERE attemptId=$id AND triggerOrdinal>$after ORDER BY triggerOrdinal LIMIT $limit;",
                ("$id", attemptId.ToString("N")), ("$after", afterTriggerOrdinal), ("$limit", limit));
            using var reader = query.ExecuteReader();
            while (reader.Read()) result.Add(new(Guid.ParseExact(reader.GetString(0), "N"), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3)));
            return result.ToImmutableArray();
        }, cancellationToken);
    }

    private static DeliveryBatchSnapshot? FindOpenBatch(SqliteConnection c, SqliteTransaction t, SectionKey key) =>
        FindBatch(c, t, "sectionKey=$key AND status=0", ("$key", StoreWire.Key(key)));
    private static DeliveryBatchSnapshot? FindBatch(SqliteConnection c, SqliteTransaction t, Guid id) =>
        FindBatch(c, t, "id=$id", ("$id", id.ToString("N")));
    private static DeliveryBatchSnapshot? FindBatch(SqliteConnection c, SqliteTransaction t, string predicate, params (string Name, object? Value)[] values)
    {
        using var query = Command(c, t, "SELECT id,sectionKey,revision,status,foldCutoff,foldCursor,foldComplete,createdUtc FROM DeliveryBatches WHERE " + predicate + ";", values);
        using var r = query.ExecuteReader();
        return r.Read() ? new(Guid.ParseExact(r.GetString(0), "N"), StoreWire.ReadKey(r.GetString(1)), r.GetInt64(2),
            (DeliveryBatchStatus)r.GetInt32(3), r.GetInt64(4), r.GetInt64(5), r.GetBoolean(6), StoreWire.Time(r.GetString(7))) : null;
    }
    private static Guid? UnresolvedBatchAttempt(SqliteConnection c, SqliteTransaction t, Guid id) =>
        Scalar(c, t, "SELECT id FROM DeliveryBatchAttempts WHERE batchId=$id AND status=0;", ("$id", id.ToString("N"))) is string text
            ? Guid.ParseExact(text, "N") : null;
    private static void UpdateBatch(SqliteConnection c, SqliteTransaction t, DeliveryBatchSnapshot b) =>
        Run(c, t, "UPDATE DeliveryBatches SET revision=$revision,status=$status,foldCutoff=$cutoff,foldCursor=$cursor,foldComplete=$complete WHERE id=$id;",
            ("$revision", b.Revision), ("$status", (int)b.Status), ("$cutoff", b.FoldCutoffOrdinal), ("$cursor", b.FoldCursorOrdinal),
            ("$complete", b.FoldComplete ? 1 : 0), ("$id", b.Id.ToString("N")));
    private static void BatchPageLimit(int value) { if (value is < 1 or > BatchDeliveryLimits.MaxPageSize) throw new ArgumentOutOfRangeException(nameof(value)); }
    private static void BatchId(Guid id) { if (id == Guid.Empty) throw new ArgumentException("A batch/attempt identity is required.", nameof(id)); }
}
