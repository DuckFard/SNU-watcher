using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Monitoring.SourcePolicy;

namespace SnuWatcher.Infrastructure.Persistence;

public sealed partial class SqliteWatchStore : ISourcePolicyStore
{
    public Task<SourcePolicySnapshot> ReadSourcePolicyAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        _ = SourcePolicySnapshot.Initial(sourceId);
        return _lane.ExecuteAsync(connection => ReadSourcePolicy(connection, null, sourceId), cancellationToken);
    }

    public Task<SourceRequestIssueResult> TryIssueSourceRequestAsync(SourceRequestIssue command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _lane.ExecuteAsync(connection =>
        {
            using var transaction = BeginWrite(connection);
            var current = ReadSourcePolicy(connection, transaction, command.SourceId);
            var now = _time.GetUtcNow();
            if (current.IsBlockedAt(now)) return new SourceRequestIssueResult(current, null);
            var ordinal = checked(current.Progress.LastIssuedOrdinal + 1);
            var progress = new SourceRequestProgress(ordinal, now, current.Progress.LatestCompletedOrdinal,
                current.Progress.LatestOutcome, current.Progress.LatestCompletedAtUtc,
                current.Progress.LastSuccessfulOrdinal, current.Progress.LastSuccessfulAtUtc);
            var updated = new SourcePolicySnapshot(current.SourceId, checked(current.Revision + 1), progress,
                current.AccessPause, current.Cooldown, current.ResumeWatermark);
            var issued = new IssuedSourceRequest(new(Guid.NewGuid(), command.SourceId, ordinal), command.Kind, now, current.ResumeWatermark);
            SaveSourcePolicy(connection, transaction, updated);
            Run(connection, transaction,
                "INSERT INTO SourceRequests(id,sourceId,ordinal,kind,issuedUtc,resumeWatermark) VALUES($id,$source,$ordinal,$kind,$at,$resume);",
                ("$id", issued.Token.Id.ToString("N")), ("$source", command.SourceId), ("$ordinal", ordinal),
                ("$kind", (int)command.Kind), ("$at", StoreWire.At(now)), ("$resume", current.ResumeWatermark));
            Checkpoint(SqliteStoreWriteStage.SourceIssued);
            transaction.Commit();
            return new SourceRequestIssueResult(updated, issued);
        }, cancellationToken);
    }

    public Task<SourcePolicyReceipt> RecordSourceRequestResultAsync(SourceRequestResultCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _lane.ExecuteAsync(connection =>
        {
            using var transaction = BeginWrite(connection);
            var issued = ReadSourceRequest(connection, transaction, command.Token);
            var facts = StoreSourcePolicyWire.ResultFacts(command);
            using (var query = Command(connection, transaction, "SELECT sourceFacts,receipt FROM SourceRequestResults WHERE requestId=$id;",
                ("$id", command.Token.Id.ToString("N"))))
            using (var reader = query.ExecuteReader())
            {
                if (reader.Read())
                {
                    if (!string.Equals(reader.GetString(0), facts, StringComparison.Ordinal)) throw new SourceRequestReplayConflictException();
                    return StoreSourcePolicyWire.ReadReceipt(reader.GetString(1));
                }
            }
            var current = ReadSourcePolicy(connection, transaction, command.Token.SourceId);
            var transition = SourcePolicyReducer.Apply(current, issued, command);
            if (transition.Applied) SaveSourcePolicy(connection, transaction, transition.Policy);
            var receipt = new SourcePolicyReceipt(command.Token, transition.Applied, transition.Policy.Revision);
            Run(connection, transaction, "INSERT INTO SourceRequestResults(requestId,sourceFacts,receipt) VALUES($id,$facts,$receipt);",
                ("$id", command.Token.Id.ToString("N")), ("$facts", facts), ("$receipt", StoreSourcePolicyWire.Receipt(receipt)));
            Checkpoint(SqliteStoreWriteStage.SourcePolicyResult);
            transaction.Commit();
            return receipt;
        }, cancellationToken);
    }

    public Task<SourceAccessResumeResult> ResumeSourceAccessAsync(SourceAccessResumeCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _lane.ExecuteAsync(connection =>
        {
            using var transaction = BeginWrite(connection);
            var current = ReadSourcePolicy(connection, transaction, command.SourceId);
            if (current.Revision != command.ExpectedRevision) return new SourceAccessResumeResult(false, current);
            var updated = SourcePolicyReducer.Resume(current);
            if (!ReferenceEquals(current, updated))
            {
                SaveSourcePolicy(connection, transaction, updated);
                Checkpoint(SqliteStoreWriteStage.SourceAccessResume);
            }
            transaction.Commit();
            return new SourceAccessResumeResult(true, updated);
        }, cancellationToken);
    }

    private static SourcePolicySnapshot ReadSourcePolicy(SqliteConnection connection, SqliteTransaction? transaction, string sourceId)
    {
        var snapshot = (string?)Scalar(connection, transaction, "SELECT snapshot FROM SourcePolicies WHERE sourceId=$source;", ("$source", sourceId));
        return snapshot is null ? SourcePolicySnapshot.Initial(sourceId) : StoreSourcePolicyWire.ReadPolicy(snapshot);
    }

    private static void SaveSourcePolicy(SqliteConnection connection, SqliteTransaction transaction, SourcePolicySnapshot value) =>
        Run(connection, transaction, "INSERT INTO SourcePolicies(sourceId,revision,issuedOrdinal,snapshot) VALUES($source,$revision,$issued,$snapshot) ON CONFLICT(sourceId) DO UPDATE SET revision=excluded.revision,issuedOrdinal=excluded.issuedOrdinal,snapshot=excluded.snapshot;",
            ("$source", value.SourceId), ("$revision", value.Revision), ("$issued", value.Progress.LastIssuedOrdinal), ("$snapshot", StoreSourcePolicyWire.Policy(value)));

    private static IssuedSourceRequest ReadSourceRequest(SqliteConnection connection, SqliteTransaction transaction, SourceRequestToken token)
    {
        using var query = Command(connection, transaction, "SELECT sourceId,ordinal,kind,issuedUtc,resumeWatermark FROM SourceRequests WHERE id=$id;",
            ("$id", token.Id.ToString("N")));
        using var reader = query.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != token.SourceId || reader.GetInt64(1) != token.Ordinal)
            throw new ArgumentException("The source request token was not issued by this store.", nameof(token));
        return new(token, (SourceRequestKind)reader.GetInt32(2), StoreWire.Time(reader.GetString(3)), reader.GetInt64(4));
    }
}
