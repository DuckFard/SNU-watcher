using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Infrastructure.Persistence;

public sealed partial class SqliteWatchStore
{
    public Task<SubscriptionSnapshot> CreateSubscriptionAsync(NewSubscription command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _lane.ExecuteAsync(connection =>
        {
            using var transaction = BeginWrite(connection);
            var result = CreateWithinTransaction(connection, transaction, command);
            transaction.Commit();
            return result.Subscription;
        }, cancellationToken);
    }

    private (SubscriptionSnapshot Subscription, bool Created) CreateWithinTransaction(SqliteConnection connection,
        SqliteTransaction transaction, NewSubscription command)
    {
        var id = command.Id.ToString("N");
        var facts = StoreWire.CreateFacts(command);
        var original = (string?)Scalar(connection, transaction, "SELECT createFacts FROM Subscriptions WHERE id=$id;", ("$id", id));
        if (original is not null)
        {
            if (!string.Equals(original, facts, StringComparison.Ordinal))
                throw new InvalidOperationException("This subscription identity was created with different original details.");
            return (ReadSubscription(connection, transaction, command.Id)!, false);
        }
        if (command.Expiry?.IsExpiredAt(_time.GetUtcNow()) == true)
            throw new InvalidOperationException("The selected watch end date has already passed.");
        if ((long)Scalar(connection, transaction, "SELECT count(*) FROM Subscriptions;")! >= PersistenceLimits.MaxSections)
            throw new InvalidOperationException("The watch collection has reached its bound.");
        var watermark = Metadata(connection, transaction, "issuedOrdinal");
        var snapshot = new SubscriptionSnapshot(command.Id, 1, 1, command.Kind, command.Label, WatchStatus.Active,
            command.InitialNoticeEnabled, command.Expiry, command.CreatedAtUtc, command.Sections.Select(k => new MemberSnapshot(k, watermark, null)));
        Run(connection, transaction, "INSERT INTO Subscriptions(id,createFacts,snapshot) VALUES($id,$facts,$snapshot);",
            ("$id", id), ("$facts", facts), ("$snapshot", StoreWire.Subscription(snapshot)));
        InsertMembers(connection, transaction, snapshot);
        AdvanceCatalog(connection, transaction);
        Checkpoint(SqliteStoreWriteStage.Subscription);
        return (snapshot, true);
    }

    public Task<SubscriptionSnapshot> ChangeSubscriptionAsync(SubscriptionChange command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _lane.ExecuteAsync(connection =>
        {
            using var transaction = BeginWrite(connection);
            var current = ReadSubscription(connection, transaction, command.Id)
                ?? throw new KeyNotFoundException("That watch no longer exists.");
            if (current.DefinitionRevision != command.ExpectedRevision) throw new StoreVersionConflictException();
            var updated = ApplyChange(current, command, Metadata(connection, transaction, "issuedOrdinal"));
            if (!ReferenceEquals(current, updated))
            {
                Run(connection, transaction, "UPDATE Subscriptions SET snapshot=$snapshot WHERE id=$id;",
                    ("$snapshot", StoreWire.Subscription(updated)), ("$id", updated.Id.ToString("N")));
                if (updated.ActivationGeneration != current.ActivationGeneration) InsertMembers(connection, transaction, updated);
                AdvanceCatalog(connection, transaction);
                Checkpoint(SqliteStoreWriteStage.Subscription);
            }
            transaction.Commit();
            return updated;
        }, cancellationToken);
    }

    private SubscriptionSnapshot ApplyChange(SubscriptionSnapshot current, SubscriptionChange change, long watermark)
    {
        if (current.Status == WatchStatus.Removed)
        {
            if (change is RemoveSubscription) return current;
            throw new InvalidOperationException("A removed watch cannot be changed or resumed.");
        }
        var status = current.Status;
        var label = current.Label;
        var expiry = current.Expiry;
        var generation = current.ActivationGeneration;
        IEnumerable<MemberSnapshot> members = current.Members;
        switch (change)
        {
            case PauseSubscription:
                if (status != WatchStatus.Active) return current;
                status = WatchStatus.Paused;
                break;
            case ResumeSubscription:
                if (expiry?.IsExpiredAt(_time.GetUtcNow()) == true)
                    throw new InvalidOperationException("Choose a later end date before resuming this watch.");
                if (status == WatchStatus.Active) return current;
                status = WatchStatus.Active;
                generation = checked(generation + 1);
                members = current.Members.Select(m => new MemberSnapshot(m.Key, watermark, null));
                break;
            case RemoveSubscription:
                status = WatchStatus.Removed;
                break;
            case RenameSubscription rename:
                if (label == rename.Label) return current;
                label = rename.Label;
                break;
            case SetSubscriptionExpiry set:
                if (Equals(expiry, set.Expiry)) return current;
                if (status == WatchStatus.Active && expiry?.IsExpiredAt(_time.GetUtcNow()) == true) status = WatchStatus.Expired;
                expiry = set.Expiry;
                if (status == WatchStatus.Active && expiry?.IsExpiredAt(_time.GetUtcNow()) == true) status = WatchStatus.Expired;
                break;
            default:
                throw new ArgumentException("Unsupported watch change.", nameof(change));
        }
        return new SubscriptionSnapshot(current.Id, checked(current.DefinitionRevision + 1), generation, current.Kind,
            label, status, current.InitialNoticeEnabled, expiry, current.CreatedAtUtc, members);
    }

    private static void InsertMembers(SqliteConnection connection, SqliteTransaction transaction, SubscriptionSnapshot subscription)
    {
        foreach (var member in subscription.Members)
        {
            EnsureIdentity(connection, transaction, member.Key);
            Run(connection, transaction,
                "INSERT INTO Members(subscriptionId,generation,sectionKey,watermark,initializedOrdinal) VALUES($id,$generation,$key,$watermark,$initialized);",
                ("$id", subscription.Id.ToString("N")), ("$generation", subscription.ActivationGeneration), ("$key", StoreWire.Key(member.Key)),
                ("$watermark", member.ActivationWatermark), ("$initialized", member.InitializedByAttemptOrdinal));
        }
    }

    private static void AdvanceCatalog(SqliteConnection connection, SqliteTransaction transaction)
    {
        var revision = checked(Metadata(connection, transaction, "catalogRevision") + 1);
        Run(connection, transaction, "UPDATE StoreMetadata SET catalogRevision=$revision WHERE id=1;", ("$revision", revision));
    }

    private static ImmutableArray<SubscriptionSnapshot> ReadSubscriptions(SqliteConnection connection, SqliteTransaction transaction)
    {
        List<string> json = [];
        using (var command = Command(connection, transaction, "SELECT snapshot FROM Subscriptions ORDER BY id LIMIT $limit;", ("$limit", PersistenceLimits.MaxSections + 1)))
        using (var reader = command.ExecuteReader()) while (reader.Read()) json.Add(reader.GetString(0));
        if (json.Count > PersistenceLimits.MaxSections) throw new InvalidDataException("The watch collection exceeds its bound.");
        return [.. json.Select(value => ReadSubscriptionValue(connection, transaction, value))];
    }

    private static SubscriptionSnapshot? ReadSubscription(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        var json = (string?)Scalar(connection, transaction, "SELECT snapshot FROM Subscriptions WHERE id=$id;", ("$id", id.ToString("N")));
        return json is null ? null : ReadSubscriptionValue(connection, transaction, json);
    }

    private static SubscriptionSnapshot ReadSubscriptionValue(SqliteConnection connection, SqliteTransaction transaction, string json)
    {
        var identity = StoreWire.SubscriptionIdentity(json);
        List<MemberSnapshot> members = [];
        using var command = Command(connection, transaction,
            "SELECT sectionKey,watermark,initializedOrdinal FROM Members WHERE subscriptionId=$id AND generation=$generation ORDER BY sectionKey LIMIT $limit;",
            ("$id", identity.Id.ToString("N")), ("$generation", identity.Generation), ("$limit", PersistenceLimits.MaxSections + 1));
        using (var reader = command.ExecuteReader()) while (reader.Read()) members.Add(new MemberSnapshot(StoreWire.ReadKey(reader.GetString(0)),
            reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        return StoreWire.ReadSubscription(json, members);
    }
}
