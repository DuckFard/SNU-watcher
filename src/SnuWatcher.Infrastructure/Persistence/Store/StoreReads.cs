using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Infrastructure.Persistence;

public sealed partial class SqliteWatchStore
{
    private static IssuedCheck ReadIssued(SqliteConnection connection, SqliteTransaction transaction, CheckAttemptToken token, SectionKey key)
    {
        DateTimeOffset issuedAt;
        using (var command = Command(connection, transaction, "SELECT ordinal,issuedUtc FROM IssuedChecks WHERE id=$id;", ("$id", token.Id.ToString("N"))))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetInt64(0) != token.Ordinal)
                throw new ArgumentException("The completion token was not issued by this store.", nameof(token));
            issuedAt = StoreWire.Time(reader.GetString(1));
        }
        List<SectionKey> scope = [];
        using (var command = Command(connection, transaction, "SELECT sectionKey FROM IssuedScopes WHERE tokenId=$id ORDER BY sectionKey LIMIT $limit;",
            ("$id", token.Id.ToString("N")), ("$limit", PersistenceLimits.MaxSections + 1)))
        using (var reader = command.ExecuteReader()) while (reader.Read()) scope.Add(StoreWire.ReadKey(reader.GetString(0)));
        if (!scope.Contains(key)) throw new ArgumentException("The section is outside the issued scope.", nameof(key));
        return new IssuedCheck(token, issuedAt, scope);
    }

    private static CommitContext ReadContext(SqliteConnection connection, SqliteTransaction transaction, CheckAttemptToken token, SectionKey key) =>
        new(ReadIssued(connection, transaction, token, key), ReadSection(connection, transaction, key),
            Metadata(connection, transaction, "catalogRevision"), RelevantSubscriptions(connection, transaction, key));

    private static IEnumerable<SubscriptionSnapshot> RelevantSubscriptions(SqliteConnection connection, SqliteTransaction transaction, SectionKey key) =>
        ReadSubscriptions(connection, transaction).Where(s => s.Members.Any(m => m.Key == key));

    private static SectionSnapshot ReadSection(SqliteConnection connection, SqliteTransaction? transaction, SectionKey key)
    {
        var json = (string?)Scalar(connection, transaction, "SELECT snapshot FROM SectionState WHERE sectionKey=$key;", ("$key", StoreWire.Key(key)));
        return json is null ? SectionSnapshot.Unchecked(key) : StoreWire.ReadSection(json);
    }

    public Task<ImmutableArray<PendingDelivery>> ReadPendingDeliveriesAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > PersistenceLimits.MaxPendingRead) throw new ArgumentOutOfRangeException(nameof(limit));
        return _lane.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            List<PendingDelivery> rows = [];
            using (var command = Command(connection, transaction, DeliverySelect + " WHERE o.status=$status ORDER BY e.createdUtc,e.id LIMIT $limit;",
                ("$status", (int)DeliveryStatus.Pending), ("$limit", limit)))
            using (var reader = command.ExecuteReader()) while (reader.Read()) rows.Add(ReadDelivery(reader));
            transaction.Commit();
            return rows.ToImmutableArray();
        }, cancellationToken);
    }

    public Task<DeliveryContext> ReadDeliveryContextAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        if (eventId == Guid.Empty) throw new ArgumentException("An event identity is required.", nameof(eventId));
        return _lane.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var delivery = FindDelivery(connection, transaction, eventId) ?? throw new KeyNotFoundException("That alert does not exist.");
            var result = new DeliveryContext(delivery, ReadSection(connection, transaction, delivery.Key), RelevantSubscriptions(connection, transaction, delivery.Key));
            transaction.Commit();
            return result;
        }, cancellationToken);
    }

    public Task<DeliveryWriteResult> RecordDeliveryResultAsync(DeliveryResultCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _lane.ExecuteAsync(connection =>
        {
            using var transaction = BeginWrite(connection);
            var current = FindDelivery(connection, transaction, command.EventId);
            if (current is null) return DeliveryWriteResult.NotFound;
            if (current.DeliveryRevision != command.ExpectedRevision || command.Attempts < current.Attempts
                || current.Status != DeliveryStatus.Pending) return DeliveryWriteResult.VersionConflict;
            var revision = checked(current.DeliveryRevision + 1);
            Run(connection, transaction, "UPDATE Outbox SET revision=$revision,status=$status,attempts=$attempts WHERE eventId=$id;",
                ("$revision", revision), ("$status", (int)command.Status), ("$attempts", command.Attempts), ("$id", command.EventId.ToString("N")));
            Run(connection, transaction, "INSERT INTO DeliveryAttempts(eventId,revision,status,attempts,recordedUtc) VALUES($id,$revision,$status,$attempts,$at);",
                ("$id", command.EventId.ToString("N")), ("$revision", revision), ("$status", (int)command.Status), ("$attempts", command.Attempts), ("$at", StoreWire.At(command.RecordedAtUtc)));
            Checkpoint(SqliteStoreWriteStage.Delivery);
            transaction.Commit();
            return DeliveryWriteResult.Applied;
        }, cancellationToken);
    }

    private static PendingDelivery? FindDelivery(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using var command = Command(connection, transaction, DeliverySelect + " WHERE e.id=$id;", ("$id", id.ToString("N")));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadDelivery(reader) : null;
    }

    private static PendingDelivery ReadDelivery(SqliteDataReader reader) => new(Guid.ParseExact(reader.GetString(0), "N"),
        reader.GetInt64(1), StoreWire.ReadNotification(reader.GetString(2)), (DeliveryStatus)reader.GetInt32(3),
        reader.GetInt32(4), StoreWire.Time(reader.GetString(5)));

    private const string DeliverySelect = "SELECT e.id,o.revision,e.trigger,o.status,o.attempts,e.createdUtc FROM Events e JOIN Outbox o ON o.eventId=e.id";
}
