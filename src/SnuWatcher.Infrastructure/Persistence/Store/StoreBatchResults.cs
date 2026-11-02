using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Persistence;

namespace SnuWatcher.Infrastructure.Persistence;

public sealed partial class SqliteWatchStore
{
    public Task<BatchAttemptReceipt> RecordBatchAttemptResultAsync(BatchAttemptResultCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _lane.ExecuteAsync(connection =>
        {
            using var transaction = BeginWrite(connection);
            var facts = StoreBatchWire.ResultFacts(command);
            using (var query = Command(connection, transaction, "SELECT sourceFacts,receipt FROM DeliveryBatchResults WHERE attemptId=$id;", ("$id", command.AttemptId.ToString("N"))))
            using (var reader = query.ExecuteReader())
                if (reader.Read())
                {
                    if (reader.GetString(0) != facts) throw new InvalidOperationException("The batch attempt already has different immutable result facts.");
                    return StoreBatchWire.ReadReceipt(reader.GetString(1));
                }
            var attempt = FindBatchAttempt(connection, transaction, command.AttemptId)
                ?? throw new KeyNotFoundException("That reserved batch attempt does not exist.");
            if (UnresolvedBatchAttempt(connection, transaction, attempt.Envelope.BatchId) != attempt.AttemptId)
                throw new InvalidDataException("The batch attempt is resolved without its required result ledger.");
            long count = 0, accepted = 0, pending = 0, failed = 0;
            foreach (var row in ReadFrozenBatchEvents(connection, transaction, attempt.AttemptId))
            {
                if (row.Revision != row.ExpectedRevision || row.Status != DeliveryStatus.Pending || row.Attempts != row.AttemptsBefore)
                    throw new InvalidDataException("A reserved event changed outside its immutable batch authority.");
                var attempts = checked(row.AttemptsBefore + (command.Outcome == BatchAttemptResultOutcome.NotSubmitted ? 0 : 1));
                var status = command.Outcome == BatchAttemptResultOutcome.OsAccepted ? DeliveryStatus.OsAccepted
                    : command.Outcome == BatchAttemptResultOutcome.NotSubmitted || attempts < attempt.Policy.MaxAttempts
                        ? DeliveryStatus.Pending : DeliveryStatus.Failed;
                WriteBatchEventResult(connection, transaction, row.Id, row.ExpectedRevision, status, attempts, command.RecordedAtUtc);
                count = checked(count + 1);
                if (status == DeliveryStatus.OsAccepted) accepted++;
                else if (status == DeliveryStatus.Pending) pending++;
                else failed++;
                Checkpoint(SqliteStoreWriteStage.BatchMembers);
            }
            if (count != attempt.Envelope.Coverage.EventCount) throw new InvalidDataException("Reserved coverage is incomplete.");
            var receipt = new BatchAttemptReceipt(attempt.AttemptId, attempt.Envelope.BatchId, command.Outcome,
                count, accepted, pending, failed, command.RecordedAtUtc);
            Run(connection, transaction, "INSERT INTO DeliveryBatchResults(attemptId,sourceFacts,receipt) VALUES($id,$facts,$receipt);",
                ("$id", attempt.AttemptId.ToString("N")), ("$facts", facts), ("$receipt", StoreBatchWire.Receipt(receipt)));
            Run(connection, transaction, "UPDATE DeliveryBatchAttempts SET status=1 WHERE id=$id;", ("$id", attempt.AttemptId.ToString("N")));
            var batch = FindBatch(connection, transaction, attempt.Envelope.BatchId)
                ?? throw new InvalidDataException("The reserved batch is missing.");
            var hasPending = (long)Scalar(connection, transaction, "SELECT EXISTS(SELECT 1 FROM DeliveryBatchEvents m JOIN Outbox o ON o.eventId=m.eventId WHERE m.batchId=$id AND o.status=$pending);",
                ("$id", batch.Id.ToString("N")), ("$pending", (int)DeliveryStatus.Pending))! != 0;
            UpdateBatch(connection, transaction, new(batch.Id, batch.Key, checked(batch.Revision + 1),
                hasPending ? DeliveryBatchStatus.Open : DeliveryBatchStatus.Closed, batch.FoldCutoffOrdinal,
                batch.FoldCursorOrdinal, batch.FoldComplete, batch.CreatedAtUtc));
            Checkpoint(SqliteStoreWriteStage.BatchResult);
            transaction.Commit();
            return receipt;
        }, cancellationToken);
    }

    private static BatchAttemptSnapshot? FindBatchAttempt(SqliteConnection c, SqliteTransaction t, Guid attemptId) =>
        Scalar(c, t, "SELECT snapshot FROM DeliveryBatchAttempts WHERE id=$id;", ("$id", attemptId.ToString("N"))) is string json
            ? StoreBatchWire.ReadAttempt(json) : null;

    private static BatchAttemptReceipt? FindBatchReceipt(SqliteConnection c, SqliteTransaction t, Guid attemptId) =>
        Scalar(c, t, "SELECT receipt FROM DeliveryBatchResults WHERE attemptId=$id;", ("$id", attemptId.ToString("N"))) is string json
            ? StoreBatchWire.ReadReceipt(json) : null;

    private static void WriteBatchEventResult(SqliteConnection c, SqliteTransaction t, Guid eventId, long expectedRevision,
        DeliveryStatus status, int attempts, DateTimeOffset at)
    {
        var revision = checked(expectedRevision + 1);
        var changed = Run(c, t, "UPDATE Outbox SET revision=$next,status=$status,attempts=$attempts WHERE eventId=$event AND revision=$expected AND status=$pending;",
            ("$next", revision), ("$status", (int)status), ("$attempts", attempts), ("$event", eventId.ToString("N")),
            ("$expected", expectedRevision), ("$pending", (int)DeliveryStatus.Pending));
        if (changed != 1) throw new InvalidDataException("The frozen outbox event lost its write authority.");
        Run(c, t, "INSERT INTO DeliveryAttempts(eventId,revision,status,attempts,recordedUtc) VALUES($event,$revision,$status,$attempts,$at);",
            ("$event", eventId.ToString("N")), ("$revision", revision), ("$status", (int)status), ("$attempts", attempts), ("$at", StoreWire.At(at)));
    }

    private static IEnumerable<FrozenBatchEvent> ReadFrozenBatchEvents(SqliteConnection c, SqliteTransaction t, Guid attemptId)
    {
        long after = 0;
        while (true)
        {
            List<FrozenBatchEvent> page = [];
            using (var query = Command(c, t, """
                SELECT f.eventId,f.triggerOrdinal,f.expectedRevision,f.attemptsBefore,o.revision,o.status,o.attempts
                FROM DeliveryBatchAttemptEvents f JOIN Outbox o ON o.eventId=f.eventId
                WHERE f.attemptId=$id AND f.triggerOrdinal>$after ORDER BY f.triggerOrdinal LIMIT $limit;
                """, ("$id", attemptId.ToString("N")), ("$after", after), ("$limit", BatchDeliveryLimits.MaxPageSize)))
            using (var reader = query.ExecuteReader())
                while (reader.Read()) page.Add(new(Guid.ParseExact(reader.GetString(0), "N"), reader.GetInt64(1), reader.GetInt64(2),
                    reader.GetInt32(3), reader.GetInt64(4), (DeliveryStatus)reader.GetInt32(5), reader.GetInt32(6)));
            if (page.Count == 0) yield break;
            after = page[^1].Ordinal;
            foreach (var row in page) yield return row;
        }
    }
    private sealed record FrozenBatchEvent(Guid Id, long Ordinal, long ExpectedRevision, int AttemptsBefore, long Revision, DeliveryStatus Status, int Attempts);
}
