using System.Text.Json;
using Microsoft.Data.Sqlite;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

/// <summary>Known V3 producer from M4 commit 8a727a85b3f9a05f48fd5f188360b3e5186ade60. Never invokes the current initializer or downgrades a newer database.</summary>
internal sealed class StoreUiV3Fixture : IDisposable
{
    private readonly StoreBatchV2Fixture _base = new();
    internal const string ProducerCommit = "8a727a85b3f9a05f48fd5f188360b3e5186ade60";
    internal const string BatchId = "44444444444444444444444444444444";
    internal const string FirstAttemptId = "55555555555555555555555555555555";
    internal const string UnresolvedAttemptId = "88888888888888888888888888888888";
    internal const string SourceRequestId = "77777777777777777777777777777777";
    internal const string Time = StoreBatchV2Fixture.SeedTime;
    internal static readonly string[] V3Tables = ["DeliveryBatches", "DeliveryBatchEvents", "DeliveryBatchAttempts", "DeliveryBatchAttemptEvents",
        "DeliveryBatchResults", "SourcePolicies", "SourceRequests", "SourceRequestResults"];

    internal StoreUiV3Fixture()
    {
        // V1/V2 slices in the reused historical fixture are byte-for-byte the same in 8a727a85b3f9a05f48fd5f188360b3e5186ade60.
        // Apply the frozen additive producer slice directly, not the future store's migration path.
        try
        {
            _base.Alter(FrozenV3Schema + "PRAGMA user_version=3;");
            SeedV3();
        }
        catch { _base.Dispose(); throw; }
    }

    internal string DatabasePath => _base.DatabasePath;
    internal SqliteConnectionFactory Factory => _base.Factory;
    internal long Version => _base.Version;
    internal string[] Backups() => _base.Backups();
    internal byte[] DatabaseHash() => _base.DatabaseHash();
    internal void Alter(string sql) => _base.Alter(sql);
    internal void SetHeader(long version, long appId) => _base.SetHeader(version, appId);
    internal string LogicalSnapshot()
    {
        using var connection = Factory.Open(SqliteOpenMode.ReadOnly);
        return LogicalSnapshot(connection);
    }
    internal static string LogicalSnapshot(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name,type,sql FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*' ORDER BY name;";
        using var reader = command.ExecuteReader();
        List<object[]> schema = [];
        while (reader.Read()) schema.Add([reader.GetString(0), reader.GetString(1), reader.GetString(2)]);
        return JsonSerializer.Serialize(new { version = StoreBatchV2Fixture.Scalar(connection, "PRAGMA user_version;"),
            application = StoreBatchV2Fixture.Scalar(connection, "PRAGMA application_id;"), schema,
            historical = HistoricalRows(connection) });
    }
    internal string HistoricalRows()
    {
        using var connection = Factory.Open(SqliteOpenMode.ReadOnly);
        return HistoricalRows(connection);
    }
    internal static string HistoricalRows(SqliteConnection connection)
    {
        List<object> added = [];
        foreach (var table in V3Tables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM " + table + " ORDER BY rowid;";
            using var reader = command.ExecuteReader();
            List<object?[]> rows = [];
            while (reader.Read())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(values.Select(v => v is DBNull ? null : v).ToArray());
            }
            added.Add(new { table, rows });
        }
        return JsonSerializer.Serialize(new { earlier = StoreBatchV2Fixture.HistoricalRows(connection), added });
    }

    private void SeedV3()
    {
        var key = StoreBatchV2Fixture.SeedKey;
        var first = Attempt(FirstAttemptId, "66666666666666666666666666666666", 1);
        var unresolved = Attempt(UnresolvedAttemptId, "99999999999999999999999999999999", 2);
        var result = JsonSerializer.Serialize(new { version = 1, id = FirstAttemptId, outcome = 2, diagnostic = "channel_failed", at = Time });
        var receipt = JsonSerializer.Serialize(new { version = 1, id = FirstAttemptId, batch = BatchId, outcome = 2,
            count = 1, accepted = 0, pending = 1, failed = 0, at = Time });
        var policy = JsonSerializer.Serialize(new { version = 1, source = "snu-sugang-v1", revision = 2, issued = 1, issuedAt = Time,
            completed = 1, outcome = 3, completedAt = Time, success = 0, successAt = (string?)null, resume = 0,
            pauseOrdinal = 1, pauseAt = Time, pauseReason = "access_restricted", cooldownAt = Time, cooldownTicks = 600000000L });
        var sourceFacts = JsonSerializer.Serialize(new { version = 1, id = SourceRequestId, source = "snu-sugang-v1", ordinal = 1,
            outcome = 3, completed = Time, started = Time, received = Time, retryKind = (int?)null, retryTicks = (long?)null,
            retryDate = (string?)null, diagnostic = "source_access_restricted" });
        var sourceReceipt = JsonSerializer.Serialize(new { version = 1, id = SourceRequestId, source = "snu-sugang-v1", ordinal = 1,
            applied = true, revision = 2 });
        using var connection = Factory.Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "INSERT INTO DeliveryBatches VALUES($batch,$key,5,0,7,7,1,1,$time); "
            + "INSERT INTO DeliveryBatchEvents VALUES($batch,$event,1,1); "
            + "INSERT INTO DeliveryBatchAttempts VALUES($first,$batch,'66666666666666666666666666666666',1,$firstSnapshot,$facts,1); "
            + "INSERT INTO DeliveryBatchAttempts VALUES($second,$batch,'99999999999999999999999999999999',2,$secondSnapshot,$facts,0); "
            + "INSERT INTO DeliveryBatchAttemptEvents VALUES($first,$event,7,1,0); INSERT INTO DeliveryBatchAttemptEvents VALUES($second,$event,7,2,1); "
            + "INSERT INTO DeliveryBatchResults VALUES($first,$result,$receipt); UPDATE Outbox SET revision=2,attempts=1; "
            + "INSERT INTO DeliveryAttempts VALUES($event,2,0,1,$time); "
            + "INSERT INTO SourcePolicies VALUES('snu-sugang-v1',2,1,$policy); "
            + "INSERT INTO SourceRequests VALUES($source,'snu-sugang-v1',1,2,$time,0); INSERT INTO SourceRequestResults VALUES($source,$sourceFacts,$sourceReceipt);",
            ("$batch", BatchId), ("$key", key), ("$time", Time), ("$event", StoreBatchV2Fixture.EventId.ToString("N")),
            ("$first", FirstAttemptId), ("$second", UnresolvedAttemptId), ("$firstSnapshot", first), ("$secondSnapshot", unresolved),
            ("$facts", new string('a', 64)), ("$result", result), ("$receipt", receipt), ("$policy", policy),
            ("$source", SourceRequestId), ("$sourceFacts", sourceFacts), ("$sourceReceipt", sourceReceipt));
        transaction.Commit();
        Assert.True(new FileInfo(DatabasePath + "-wal").Length > 0);
    }
    private static string Attempt(string id, string request, int sequence) => JsonSerializer.Serialize(new
    {
        version = 1, id, request, sequence, prepared = Time, ageTicks = 1200000000L, maxAttempts = 3, batch = BatchId,
        key = StoreBatchV2Fixture.SeedKey, registered = 119, capacity = 120, success = Time, ordinal = 7,
        recipients = new[] { new { id = StoreBatchV2Fixture.SubscriptionId.ToString("N"), generation = 2, reasons = new[] { 1 } } },
        coverage = new { count = 1, digest = new string('b', 64), min = 7, max = 7 },
    });
    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object Value)[] values)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }
    public void Dispose() => _base.Dispose();

    // Frozen verbatim from 8a727a85b3f9a05f48fd5f188360b3e5186ade60: StoreBatchSchema.BatchSchemaSql.
    private const string FrozenV3Schema = """
        CREATE TABLE DeliveryBatches(id TEXT PRIMARY KEY,sectionKey TEXT NOT NULL REFERENCES SectionIdentities(sectionKey),revision INTEGER NOT NULL CHECK(revision>0),status INTEGER NOT NULL CHECK(status IN(0,1)),foldCutoff INTEGER NOT NULL CHECK(foldCutoff>=0),foldCursor INTEGER NOT NULL CHECK(foldCursor>=0),foldComplete INTEGER NOT NULL CHECK(foldComplete IN(0,1)),foldAfterAttempt INTEGER NOT NULL DEFAULT 0 CHECK(foldAfterAttempt>=0),createdUtc TEXT NOT NULL);
        CREATE UNIQUE INDEX OpenDeliveryBatch ON DeliveryBatches(sectionKey) WHERE status=0;
        CREATE TABLE DeliveryBatchEvents(batchId TEXT NOT NULL REFERENCES DeliveryBatches(id),eventId TEXT NOT NULL UNIQUE REFERENCES Events(id),claimedRevision INTEGER NOT NULL CHECK(claimedRevision>0),joinedRevision INTEGER NOT NULL CHECK(joinedRevision>0),PRIMARY KEY(batchId,eventId));
        CREATE TABLE DeliveryBatchAttempts(id TEXT PRIMARY KEY,batchId TEXT NOT NULL REFERENCES DeliveryBatches(id),reservationId TEXT NOT NULL UNIQUE,sequence INTEGER NOT NULL CHECK(sequence>0),snapshot TEXT NOT NULL,preparedFacts TEXT NOT NULL,status INTEGER NOT NULL CHECK(status IN(0,1)),UNIQUE(batchId,sequence));
        CREATE UNIQUE INDEX UnresolvedBatchAttempt ON DeliveryBatchAttempts(batchId) WHERE status=0;
        CREATE TABLE DeliveryBatchAttemptEvents(attemptId TEXT NOT NULL REFERENCES DeliveryBatchAttempts(id),eventId TEXT NOT NULL REFERENCES Events(id),triggerOrdinal INTEGER NOT NULL CHECK(triggerOrdinal>0),expectedRevision INTEGER NOT NULL CHECK(expectedRevision>0),attemptsBefore INTEGER NOT NULL CHECK(attemptsBefore>=0),PRIMARY KEY(attemptId,eventId));
        CREATE INDEX BatchAttemptCoverage ON DeliveryBatchAttemptEvents(attemptId,triggerOrdinal);
        CREATE TABLE DeliveryBatchResults(attemptId TEXT PRIMARY KEY REFERENCES DeliveryBatchAttempts(id),sourceFacts TEXT NOT NULL,receipt TEXT NOT NULL);
        CREATE TABLE SourcePolicies(sourceId TEXT PRIMARY KEY,revision INTEGER NOT NULL CHECK(revision>=0),issuedOrdinal INTEGER NOT NULL CHECK(issuedOrdinal>=0),snapshot TEXT NOT NULL);
        CREATE TABLE SourceRequests(id TEXT PRIMARY KEY,sourceId TEXT NOT NULL REFERENCES SourcePolicies(sourceId),ordinal INTEGER NOT NULL CHECK(ordinal>0),kind INTEGER NOT NULL,issuedUtc TEXT NOT NULL,resumeWatermark INTEGER NOT NULL CHECK(resumeWatermark>=0),UNIQUE(sourceId,ordinal));
        CREATE TABLE SourceRequestResults(requestId TEXT PRIMARY KEY REFERENCES SourceRequests(id),sourceFacts TEXT NOT NULL,receipt TEXT NOT NULL);
        """;
}
