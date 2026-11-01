using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Observations;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Infrastructure.Persistence;

public sealed partial class SqliteWatchStore
{
    public Task<TryCommitResult> TryCommitSectionAsync(PreparedSectionCommit command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _lane.ExecuteAsync(connection => CommitSection(connection, command), cancellationToken);
    }

    private TryCommitResult CommitSection(SqliteConnection connection, PreparedSectionCommit command)
    {
        using var transaction = BeginWrite(connection);
        var completion = command.Completion;
        _ = ReadIssued(connection, transaction, completion.Token, completion.Key);
        var facts = StoreWire.SourceFacts(completion);
        var replay = ReadReplay(connection, transaction, completion, facts);
        if (replay is not null)
        {
            transaction.Commit();
            return TryCommitResult.Replayed(replay);
        }

        var context = ReadContext(connection, transaction, completion.Token, completion.Key);
        if (context.Section.Version != command.ExpectedSectionVersion || context.CatalogRevision != command.ExpectedCatalogRevision)
            return TryCommitResult.VersionConflict();

        // A prepared command is not trusted merely because its DTO is well formed. Validate semantic effects
        // against the current consistent state; never compare record/list reference equality.
        var expected = ObservationPreparer.Prepare(context, completion, command.EvaluatedAtUtc);
        if (!string.Equals(StoreWire.Effects(expected.Effects), StoreWire.Effects(command.Effects), StringComparison.Ordinal))
            throw new ArgumentException("Prepared effects do not match the issued observation and current state.", nameof(command));
        var current = ObservationPreparer.Prepare(context, completion, _time.GetUtcNow());
        if (!string.Equals(StoreWire.Effects(current.Effects), StoreWire.Effects(command.Effects), StringComparison.Ordinal))
            return TryCommitResult.VersionConflict(); // Expiry can cross its boundary without a catalog edit.

        var stale = completion.Token.Ordinal <= context.Section.LatestCompletedOrdinal;
        var version = context.Section.Version;
        Guid? eventId = null;
        if (!stale)
        {
            version = checked(version + 1);
            PersistSection(connection, transaction, context.Section, completion, command.Effects, version);
            Checkpoint(SqliteStoreWriteStage.Section);
            PersistInitializations(connection, transaction, completion, command.Effects);
            Checkpoint(SqliteStoreWriteStage.Members);
            if (command.Effects.Notification is { } notification)
                eventId = PersistEvent(connection, transaction, notification, _time.GetUtcNow());
        }
        var receipt = new SectionCommitReceipt(completion.Token, completion.Key,
            stale ? CommitDisposition.IgnoredStale : Disposition(command.Effects.EvaluationOutcome), version, eventId);
        Run(connection, transaction, "INSERT INTO Completions(tokenId,sectionKey,sourceFacts,receipt) VALUES($token,$key,$facts,$receipt);",
            ("$token", completion.Token.Id.ToString("N")), ("$key", StoreWire.Key(completion.Key)),
            ("$facts", facts), ("$receipt", StoreWire.Receipt(receipt)));
        Checkpoint(SqliteStoreWriteStage.Receipt);
        transaction.Commit();
        return TryCommitResult.Committed(receipt);
    }

    private static SectionCommitReceipt? ReadReplay(SqliteConnection connection, SqliteTransaction transaction,
        SectionCompletion completion, string facts)
    {
        using var command = Command(connection, transaction,
            "SELECT sourceFacts,receipt FROM Completions WHERE tokenId=$token AND sectionKey=$key;",
            ("$token", completion.Token.Id.ToString("N")), ("$key", StoreWire.Key(completion.Key)));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (!string.Equals(reader.GetString(0), facts, StringComparison.Ordinal)) throw new ObservationReplayConflictException();
        var receipt = StoreWire.ReadReceipt(reader.GetString(1));
        if (receipt.Token != completion.Token || receipt.Key != completion.Key)
            throw new InvalidDataException("The stored completion receipt identity is invalid.");
        return receipt;
    }

    private static void PersistSection(SqliteConnection connection, SqliteTransaction transaction, SectionSnapshot previous,
        SectionCompletion completion, SectionEffects effects, long version)
    {
        var baseline = effects.NextBaseline ?? previous.Baseline;
        var next = new SectionSnapshot(completion.Key, version, completion.Token.Ordinal, effects.HealthOutcome,
            completion.ObservedAtUtc, baseline?.ObservedAt, baseline);
        Run(connection, transaction,
            "INSERT INTO SectionState(sectionKey,snapshot) VALUES($key,$snapshot) ON CONFLICT(sectionKey) DO UPDATE SET snapshot=excluded.snapshot;",
            ("$key", StoreWire.Key(completion.Key)), ("$snapshot", StoreWire.Section(next)));
    }

    private static void PersistInitializations(SqliteConnection connection, SqliteTransaction transaction,
        SectionCompletion completion, SectionEffects effects)
    {
        foreach (var initialization in effects.Initializations)
        {
            var affected = Run(connection, transaction,
                "UPDATE Members SET initializedOrdinal=$ordinal WHERE subscriptionId=$id AND generation=$generation AND sectionKey=$key AND initializedOrdinal IS NULL AND watermark<$ordinal;",
                ("$ordinal", completion.Token.Ordinal), ("$id", initialization.SubscriptionId.ToString("N")),
                ("$generation", initialization.Generation), ("$key", StoreWire.Key(completion.Key)));
            if (affected != 1) throw new InvalidOperationException("The member initialization changed before commit.");
        }
    }

    private Guid PersistEvent(SqliteConnection connection, SqliteTransaction transaction, OpeningNotification notification, DateTimeOffset createdAt)
    {
        var id = Guid.NewGuid();
        Run(connection, transaction, "INSERT INTO Events(id,sectionKey,triggerOrdinal,trigger,createdUtc) VALUES($id,$key,$ordinal,$trigger,$created);",
            ("$id", id.ToString("N")), ("$key", StoreWire.Key(notification.Key)), ("$ordinal", notification.AttemptSequence),
            ("$trigger", StoreWire.Notification(notification)), ("$created", StoreWire.At(createdAt)));
        Checkpoint(SqliteStoreWriteStage.Event);
        foreach (var recipient in notification.Recipients)
            Run(connection, transaction, "INSERT INTO EventRecipients(eventId,subscriptionId,generation,reason) VALUES($event,$subscription,$generation,$reason);",
                ("$event", id.ToString("N")), ("$subscription", Guid.ParseExact(recipient.SubscriptionId, "N").ToString("N")),
                ("$generation", recipient.Generation), ("$reason", (int)recipient.Reason));
        Checkpoint(SqliteStoreWriteStage.Recipients);
        Run(connection, transaction, "INSERT INTO Outbox(eventId,revision,status,attempts) VALUES($id,1,$status,0);",
            ("$id", id.ToString("N")), ("$status", (int)DeliveryStatus.Pending));
        Checkpoint(SqliteStoreWriteStage.Outbox);
        return id;
    }

    private static CommitDisposition Disposition(EvaluationOutcome outcome) => outcome switch
    {
        EvaluationOutcome.BaselineEstablished or EvaluationOutcome.Opening or EvaluationOutcome.NoOpening => CommitDisposition.AcceptedValid,
        EvaluationOutcome.IgnoredUnknown => CommitDisposition.AcceptedUnknown,
        EvaluationOutcome.RevalidationRequired => CommitDisposition.RevalidationRequired,
        _ => throw new InvalidOperationException("Unexpected completion disposition."),
    };
}
