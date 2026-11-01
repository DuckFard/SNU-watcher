using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Infrastructure.Persistence;

/// <summary>Durable, transactional watch state. All database work runs on one bounded background lane.</summary>
public sealed partial class SqliteWatchStore : IWatchStore, IAsyncDisposable
{
    private readonly SqliteCommandLane _lane;
    private readonly TimeProvider _time;
    private readonly Action<SqliteStoreWriteStage>? _fault;

    private SqliteWatchStore(SqliteCommandLane lane, TimeProvider time, Action<SqliteStoreWriteStage>? fault)
    { _lane = lane; _time = time; _fault = fault; }

    public static async Task<SqliteWatchStore> OpenAsync(SqliteConnectionFactory factory, TimeProvider? timeProvider = null,
        Action<SqliteStoreWriteStage>? faultInjector = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var store = new SqliteWatchStore(new SqliteCommandLane(factory), timeProvider ?? TimeProvider.System, faultInjector);
        try
        {
            await store._lane.ExecuteAsync(connection => { store.Initialize(connection); return true; }, cancellationToken).ConfigureAwait(false);
            return store;
        }
        catch { await store.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public ValueTask DisposeAsync() => _lane.DisposeAsync();

    public Task<IssuedCheck> IssueCheckAsync(CheckIssue issue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return _lane.ExecuteAsync(connection =>
        {
            using var transaction = BeginWrite(connection);
            var ordinal = checked(Metadata(connection, transaction, "issuedOrdinal") + 1);
            var token = new CheckAttemptToken(Guid.NewGuid(), ordinal);
            Run(connection, transaction, "UPDATE StoreMetadata SET issuedOrdinal=$ordinal WHERE id=1;", ("$ordinal", ordinal));
            Run(connection, transaction, "INSERT INTO IssuedChecks(id,ordinal,issuedUtc) VALUES($id,$ordinal,$at);",
                ("$id", token.Id.ToString("N")), ("$ordinal", ordinal), ("$at", StoreWire.At(issue.IssuedAtUtc)));
            foreach (var key in issue.Scope)
            {
                EnsureIdentity(connection, transaction, key);
                Run(connection, transaction, "INSERT INTO IssuedScopes(tokenId,sectionKey) VALUES($id,$key);",
                    ("$id", token.Id.ToString("N")), ("$key", StoreWire.Key(key)));
            }
            Checkpoint(SqliteStoreWriteStage.Issued);
            transaction.Commit();
            return new IssuedCheck(token, issue.IssuedAtUtc, issue.Scope);
        }, cancellationToken);
    }

    public Task<ImmutableArray<SubscriptionSnapshot>> ListSubscriptionsAsync(CancellationToken cancellationToken = default) =>
        _lane.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var result = ReadSubscriptions(connection, transaction).Where(s => s.Status != WatchStatus.Removed).ToImmutableArray();
            transaction.Commit();
            return result;
        }, cancellationToken);

    public Task<SubscriptionSnapshot?> FindSubscriptionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("A subscription identity is required.", nameof(id));
        return _lane.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var result = ReadSubscription(connection, transaction, id);
            transaction.Commit();
            return result;
        }, cancellationToken);
    }

    public Task<SectionSnapshot> ReadSectionAsync(SectionKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _lane.ExecuteAsync(connection => ReadSection(connection, null, key), cancellationToken);
    }

    public Task<CommitContext> ReadCommitContextAsync(CheckAttemptToken token, SectionKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(key);
        return _lane.ExecuteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var result = ReadContext(connection, transaction, token, key);
            transaction.Commit();
            return result;
        }, cancellationToken);
    }

    private static SqliteTransaction BeginWrite(SqliteConnection connection)
    {
        Run(connection, null, "PRAGMA synchronous=FULL;");
        return connection.BeginTransaction(deferred: false);
    }

    private void Checkpoint(SqliteStoreWriteStage stage) => _fault?.Invoke(stage);

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static int Run(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] values)
    {
        using var command = Command(connection, transaction, sql, values);
        return command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] values)
    {
        using var command = Command(connection, transaction, sql, values);
        var result = command.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    private static long Metadata(SqliteConnection connection, SqliteTransaction transaction, string column) => column switch
    {
        "issuedOrdinal" => (long)Scalar(connection, transaction, "SELECT issuedOrdinal FROM StoreMetadata WHERE id=1;")!,
        "catalogRevision" => (long)Scalar(connection, transaction, "SELECT catalogRevision FROM StoreMetadata WHERE id=1;")!,
        _ => throw new ArgumentException("Unsupported metadata field.", nameof(column)),
    };
}

/// <summary>Optional narrow test fault boundary. Production callers leave the observer absent.</summary>
public enum SqliteStoreWriteStage { SchemaValidated, SchemaCreated, Issued, Subscription, Section, Members, Event, Recipients, Outbox, Receipt, Delivery }
