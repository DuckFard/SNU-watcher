using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Persistence;

namespace SnuWatcher.Infrastructure.Persistence;

public sealed partial class SqliteWatchStore : IBatchDeliveryStore
{
    public Task<BatchReservationResult> TryReserveBatchAttemptAsync(PreparedBatchAttempt command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _lane.ExecuteAsync(connection =>
        {
            using var transaction = BeginWrite(connection);
            var fingerprint = StoreBatchWire.PreparedFacts(command);
            var replay = FindReservationReplay(connection, transaction, command.ReservationRequestId, fingerprint);
            if (replay is not null) return replay;
            var expected = command.Context;
            var batch = FindBatch(connection, transaction, expected.Batch.Id)
                ?? throw new KeyNotFoundException("That delivery batch does not exist.");
            var unresolved = UnresolvedBatchAttempt(connection, transaction, batch.Id);
            if (unresolved is not null)
                return new BatchReservationResult(BatchReservationStatus.UnresolvedAttempt, batch, null, unresolved, "attempt_unresolved");
            if (batch.Revision != expected.Batch.Revision || ReadSection(connection, transaction, batch.Key).Version != expected.Section.Version
                || Metadata(connection, transaction, "catalogRevision") != expected.CatalogRevision)
                return BatchWithoutAttempt(BatchReservationStatus.VersionConflict, batch, "snapshot_changed");
            var context = ReadBatchContext(connection, transaction, batch, expected.Policy, _time.GetUtcNow());
            var sealedAt = _time.GetUtcNow();
            var decision = BatchDeliveryPlanner.Plan(context, sealedAt);
            if (StoreBatchWire.ContextFacts(context) != StoreBatchWire.ContextFacts(expected)
                || StoreBatchWire.DecisionFacts(decision) != StoreBatchWire.DecisionFacts(command.Decision))
                return BatchWithoutAttempt(BatchReservationStatus.VersionConflict, batch, "eligibility_changed");
            if (decision.Disposition is not (BatchPlanDisposition.Send or BatchPlanDisposition.ResolveWithoutSend))
                return BatchWithoutAttempt(ReservationStatus(decision.Disposition), batch, decision.ReasonCode);

            var attempt = decision.Disposition == BatchPlanDisposition.Send
                ? CreateBatchAttempt(connection, transaction, command, context, sealedAt, fingerprint) : null;
            ResolveOrFreezeBatchMembers(connection, transaction, context, attempt, sealedAt);
            batch = new(batch.Id, batch.Key, checked(batch.Revision + 1),
                attempt is null ? DeliveryBatchStatus.Closed : DeliveryBatchStatus.Open,
                batch.FoldCutoffOrdinal, batch.FoldCursorOrdinal, batch.FoldComplete, batch.CreatedAtUtc);
            UpdateBatch(connection, transaction, batch);
            Checkpoint(SqliteStoreWriteStage.BatchReserved);
            transaction.Commit();
            return attempt is null ? BatchWithoutAttempt(BatchReservationStatus.Closed, batch, decision.ReasonCode)
                : new BatchReservationResult(BatchReservationStatus.Reserved, batch, attempt, null, "reserved");
        }, cancellationToken);
    }

    private static BatchReservationResult? FindReservationReplay(SqliteConnection c, SqliteTransaction t, Guid requestId, string fingerprint)
    {
        Guid? id = null;
        using (var query = Command(c, t, "SELECT id,preparedFacts FROM DeliveryBatchAttempts WHERE reservationId=$id;", ("$id", requestId.ToString("N"))))
        using (var reader = query.ExecuteReader())
            if (reader.Read())
            {
                if (reader.GetString(1) != fingerprint) throw new InvalidOperationException("The reservation identity already has different immutable prepared facts.");
                id = Guid.ParseExact(reader.GetString(0), "N");
            }
        if (id is null) return null;
        var attempt = FindBatchAttempt(c, t, id.Value) ?? throw new InvalidDataException("Reservation replay is missing its attempt.");
        var batch = FindBatch(c, t, attempt.Envelope.BatchId) ?? throw new InvalidDataException("Reservation replay is missing its batch.");
        return new(BatchReservationStatus.Replayed, batch, attempt, null, "reservation_replayed", FindBatchReceipt(c, t, attempt.AttemptId));
    }

    private static BatchAttemptSnapshot CreateBatchAttempt(SqliteConnection c, SqliteTransaction t, PreparedBatchAttempt command,
        BatchDeliveryContext context, DateTimeOffset at, string fingerprint)
    {
        var content = command.Decision.Content ?? throw new InvalidDataException("A send decision requires content.");
        var id = Guid.NewGuid();
        var sequence = checked(((long?)Scalar(c, t, "SELECT max(sequence) FROM DeliveryBatchAttempts WHERE batchId=$id;",
            ("$id", context.Batch.Id.ToString("N"))) ?? 0) + 1);
        var attempt = new BatchAttemptSnapshot(id, command.ReservationRequestId, sequence, at, context.Policy,
            new(context.Batch.Id, id, content.Key, content.CurrentCounts, content.CurrentSuccessAtUtc,
                content.CurrentSourceOrdinal, content.EligibleRecipients, content.Coverage));
        Run(c, t, "INSERT INTO DeliveryBatchAttempts(id,batchId,reservationId,sequence,snapshot,preparedFacts,status) VALUES($id,$batch,$request,$sequence,$snapshot,$facts,0);",
            ("$id", id.ToString("N")), ("$batch", context.Batch.Id.ToString("N")), ("$request", command.ReservationRequestId.ToString("N")),
            ("$sequence", sequence), ("$snapshot", StoreBatchWire.Attempt(attempt)), ("$facts", fingerprint));
        return attempt;
    }

    private void ResolveOrFreezeBatchMembers(SqliteConnection c, SqliteTransaction t, BatchDeliveryContext context,
        BatchAttemptSnapshot? attempt, DateTimeOffset at)
    {
        Dictionary<Guid, SubscriptionSnapshot?> cache = [];
        long covered = 0;
        foreach (var row in ReadPendingBatchEvents(c, t, context.Batch.Id))
        {
            var eligible = EligibleBatchAssociations(c, t, row, context.Batch.Key, context.EvaluatedAtUtc, cache).Count != 0;
            var exhausted = eligible && row.Attempts >= context.Policy.MaxAttempts;
            if (attempt is not null && eligible && !exhausted)
            {
                Run(c, t, "INSERT INTO DeliveryBatchAttemptEvents(attemptId,eventId,triggerOrdinal,expectedRevision,attemptsBefore) VALUES($attempt,$event,$ordinal,$revision,$attempts);",
                    ("$attempt", attempt.AttemptId.ToString("N")), ("$event", row.Id.ToString("N")), ("$ordinal", row.Ordinal),
                    ("$revision", row.Revision), ("$attempts", row.Attempts));
                covered = checked(covered + 1);
            }
            else WriteBatchEventResult(c, t, row.Id, row.Revision, exhausted ? DeliveryStatus.Failed : DeliveryStatus.Superseded, row.Attempts, at);
            Checkpoint(SqliteStoreWriteStage.BatchMembers);
        }
        if (attempt is not null && covered != context.EligibleCoverage.EventCount)
            throw new InvalidDataException("The sealed batch coverage changed while holding its write reservation.");
    }

    private static BatchReservationResult BatchWithoutAttempt(BatchReservationStatus status, DeliveryBatchSnapshot batch, string reason) =>
        new(status, batch, null, null, reason);
    private static BatchReservationStatus ReservationStatus(BatchPlanDisposition disposition) => disposition switch
    {
        BatchPlanDisposition.NeedsFreshCheck => BatchReservationStatus.NeedsFreshCheck,
        BatchPlanDisposition.NeedsRevalidation => BatchReservationStatus.NeedsRevalidation,
        BatchPlanDisposition.MoreToFold => BatchReservationStatus.MoreToFold,
        BatchPlanDisposition.Closed => BatchReservationStatus.Closed,
        BatchPlanDisposition.RecipientLimitReached => BatchReservationStatus.RecipientLimitReached,
        _ => throw new InvalidDataException("This planner result requires explicit immutable attempt authority."),
    };
}
