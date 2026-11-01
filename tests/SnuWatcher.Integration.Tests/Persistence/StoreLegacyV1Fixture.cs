using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

/// <summary>
/// A genuine frozen V1 database from commit 05c90d3. This fixture never opens the current store
/// or relabels a current schema. It checkpoints the empty V1 schema before putting the seed only in WAL.
/// </summary>
internal sealed class StoreLegacyV1Fixture : IDisposable
{
    private const string Prefix = "SnuWatcher-v1-migration-";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _writer;
    internal const long AppId = 1397642583;
    internal static readonly Guid SubscriptionId = Guid.ParseExact("11111111111111111111111111111111", "N");
    internal static readonly Guid TokenId = Guid.ParseExact("22222222222222222222222222222222", "N");
    internal static readonly Guid EventId = Guid.ParseExact("33333333333333333333333333333333", "N");
    internal static readonly SectionKey Key = new(2026, "U000200002", "U000300002", "F32.102", "012", "0");
    private const string SeedKey = "[2026,\"U000200002\",\"U000300002\",\"F32.102\",\"012\",\"0\"]";
    private const string SeedTime = "2026-11-01T00:00:00.0000000+00:00";

    internal StoreLegacyV1Fixture()
    {
        Directory.CreateDirectory(_directory);
        Factory = new SqliteConnectionFactory(DatabasePath);
        _writer = Factory.Open();
        try
        {
            Execute(_writer, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
            using (var transaction = _writer.BeginTransaction())
            {
                Execute(_writer, FrozenV1Schema, transaction);
                Execute(_writer, "PRAGMA application_id=1397642583; PRAGMA user_version=1;", transaction);
                transaction.Commit();
            }
            Execute(_writer, "PRAGMA wal_checkpoint(TRUNCATE);");
            var beforeSeed = DatabaseHash();
            Seed();
            Assert.Equal(beforeSeed, DatabaseHash());
            Assert.True(new FileInfo(DatabasePath + "-wal").Length > 0, "The seed must be committed only in WAL.");
        }
        catch
        {
            _writer.Dispose();
            DeleteDirectory();
            throw;
        }
    }

    internal string DatabasePath => Path.Combine(_directory, "legacy-v1.sqlite3");
    internal SqliteConnectionFactory Factory { get; }
    internal string[] Backups() => Directory.GetFiles(_directory, "legacy-v1.sqlite3.before-*.sqlite3");
    internal byte[] DatabaseHash()
    {
        using var stream = new FileStream(DatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(stream);
    }
    internal long Version => Convert.ToInt64(Scalar(_writer, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
    internal void SetHeader(long version, long applicationId) => Execute(_writer,
        "PRAGMA user_version=" + version.ToString(CultureInfo.InvariantCulture)
        + "; PRAGMA application_id=" + applicationId.ToString(CultureInfo.InvariantCulture) + ";");

    internal string LogicalSnapshot() => LogicalSnapshot(_writer);

    internal static string LogicalSnapshot(SqliteConnection connection)
    {
        // The fixed allowlist is the historical schema, not a reflection over current production tables.
        var snapshot = new List<object?>
        {
            Scalar(connection, "PRAGMA user_version;"), Scalar(connection, "PRAGMA application_id;")
        };
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT type,name,sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name;";
            using var reader = command.ExecuteReader();
            var schema = new List<object?[]>();
            while (reader.Read()) schema.Add(Row(reader));
            snapshot.Add(schema);
        }
        foreach (var table in new[] { "StoreMetadata", "SectionIdentities", "IssuedChecks", "IssuedScopes", "Subscriptions", "Members",
            "SectionState", "Completions", "Events", "EventRecipients", "Outbox", "DeliveryAttempts" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM " + table + " ORDER BY rowid;";
            using var reader = command.ExecuteReader();
            var rows = new List<object?[]>();
            while (reader.Read()) rows.Add(Row(reader));
            snapshot.Add(new { table, rows });
        }
        return JsonSerializer.Serialize(snapshot);
    }

    internal static void AssertBackupHeader(string path)
    {
        var header = new byte[100];
        using var stream = File.OpenRead(path);
        stream.ReadExactly(header);
        Assert.Equal("SQLite format 3\0", System.Text.Encoding.ASCII.GetString(header, 0, 16));
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(60, 4)));
        Assert.Equal(AppId, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(68, 4)));
    }

    private void Seed()
    {
        // Explicit historical wire projections: independent of StoreWire or current DTO serializers.
        const string subscription = """{"id":"11111111111111111111111111111111","revision":5,"generation":2,"kind":0,"label":"Frozen V1 \uC11C\uC6B8","status":0,"initial":true,"expiry":null,"created":"2026-11-01T00:00:00.0000000+00:00"}""";
        var quotedKey = JsonSerializer.Serialize(SeedKey);
        var createFacts = "{\"version\":1,\"id\":\"11111111111111111111111111111111\",\"label\":\"Frozen V1 \\uC11C\\uC6B8\",\"kind\":0,\"sections\":[" + quotedKey + "],\"initial\":true,\"expiry\":null,\"created\":\"" + SeedTime + "\"}";
        var section = "{\"key\":" + quotedKey + ",\"version\":4,\"ordinal\":7,\"outcome\":0,\"attempted\":\"" + SeedTime
            + "\",\"success\":\"" + SeedTime + "\",\"baseline\":{\"registered\":119,\"capacity\":120,\"meaning\":\"enrolled-over-quota\",\"ordinal\":7,\"observed\":\"" + SeedTime + "\"}}";
        var trigger = "{\"key\":" + quotedKey + ",\"seats\":1,\"previous\":0,\"ordinal\":7,\"observed\":\"" + SeedTime
            + "\",\"recipients\":[{\"id\":\"11111111111111111111111111111111\",\"generation\":2,\"reason\":1}]}";
        var sourceFacts = "{\"version\":1,\"token\":\"22222222222222222222222222222222\",\"ordinal\":7,\"key\":" + quotedKey
            + ",\"observed\":\"" + SeedTime + "\",\"kind\":0,\"registered\":119,\"capacity\":120,\"meaning\":\"enrolled-over-quota\",\"outcome\":0,\"diagnostic\":null}";
        var receipt = "{\"token\":\"22222222222222222222222222222222\",\"ordinal\":7,\"key\":" + quotedKey
            + ",\"disposition\":0,\"version\":4,\"eventId\":\"33333333333333333333333333333333\"}";
        using var transaction = _writer.BeginTransaction();
        Execute(_writer, "UPDATE StoreMetadata SET issuedOrdinal=7,catalogRevision=3 WHERE id=1;", transaction);
        Execute(_writer, "INSERT INTO SectionIdentities VALUES($key,2026,'U000200002','U000300002','F32.102','012','0');", transaction, ("$key", SeedKey));
        Execute(_writer, "INSERT INTO IssuedChecks VALUES($token,7,$time); INSERT INTO IssuedScopes VALUES($token,$key);", transaction,
            ("$token", TokenId.ToString("N")), ("$time", SeedTime), ("$key", SeedKey));
        Execute(_writer, "INSERT INTO Subscriptions VALUES($subscription,$facts,$snapshot); INSERT INTO Members VALUES($subscription,2,$key,0,7);", transaction,
            ("$subscription", SubscriptionId.ToString("N")), ("$facts", createFacts), ("$snapshot", subscription), ("$key", SeedKey));
        Execute(_writer, "INSERT INTO SectionState VALUES($key,$snapshot); INSERT INTO Completions VALUES($token,$key,$facts,$receipt);", transaction,
            ("$key", SeedKey), ("$snapshot", section), ("$token", TokenId.ToString("N")), ("$facts", sourceFacts), ("$receipt", receipt));
        Execute(_writer, "INSERT INTO Events VALUES($event,$key,7,$trigger,$time); INSERT INTO EventRecipients VALUES($event,$subscription,2,1); INSERT INTO Outbox VALUES($event,1,0,0);", transaction,
            ("$event", EventId.ToString("N")), ("$key", SeedKey), ("$trigger", trigger), ("$time", SeedTime), ("$subscription", SubscriptionId.ToString("N")));
        transaction.Commit();
    }

    private static object?[] Row(SqliteDataReader reader)
    {
        var values = new object[reader.FieldCount];
        reader.GetValues(values);
        return values.Select(value => value is DBNull ? null : value).ToArray();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _writer.Dispose();
        DeleteDirectory();
    }

    private void DeleteDirectory()
    {
        var target = Path.GetFullPath(_directory);
        if (!string.Equals(Path.GetDirectoryName(target), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(target).StartsWith(Prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(Path.GetFileName(target)[Prefix.Length..], "N", out _))
            throw new InvalidOperationException("Refusing cleanup outside the migration fixture's unique temporary directory.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }

    // Frozen verbatim from 05c90d3: StoreSchema.SchemaSql (schema version 1).
    private const string FrozenV1Schema = """
        CREATE TABLE StoreMetadata(id INTEGER PRIMARY KEY CHECK(id=1), issuedOrdinal INTEGER NOT NULL CHECK(issuedOrdinal>=0), catalogRevision INTEGER NOT NULL CHECK(catalogRevision>=0));
        INSERT INTO StoreMetadata VALUES(1,0,0);
        CREATE TABLE SectionIdentities(sectionKey TEXT PRIMARY KEY,year INTEGER NOT NULL,semester TEXT NOT NULL,detail TEXT NOT NULL,course TEXT NOT NULL,section TEXT NOT NULL,subsubject TEXT NOT NULL,UNIQUE(year,semester,detail,course,section,subsubject));
        CREATE TABLE IssuedChecks(id TEXT PRIMARY KEY, ordinal INTEGER NOT NULL UNIQUE CHECK(ordinal>0), issuedUtc TEXT NOT NULL);
        CREATE TABLE IssuedScopes(tokenId TEXT NOT NULL REFERENCES IssuedChecks(id),sectionKey TEXT NOT NULL REFERENCES SectionIdentities(sectionKey),PRIMARY KEY(tokenId,sectionKey));
        CREATE TABLE Subscriptions(id TEXT PRIMARY KEY,createFacts TEXT NOT NULL,snapshot TEXT NOT NULL);
        CREATE TABLE Members(subscriptionId TEXT NOT NULL REFERENCES Subscriptions(id),generation INTEGER NOT NULL CHECK(generation>0),sectionKey TEXT NOT NULL REFERENCES SectionIdentities(sectionKey),watermark INTEGER NOT NULL CHECK(watermark>=0),initializedOrdinal INTEGER REFERENCES IssuedChecks(ordinal),PRIMARY KEY(subscriptionId,generation,sectionKey),CHECK(initializedOrdinal IS NULL OR initializedOrdinal>watermark));
        CREATE TABLE SectionState(sectionKey TEXT PRIMARY KEY REFERENCES SectionIdentities(sectionKey),snapshot TEXT NOT NULL);
        CREATE TABLE Completions(tokenId TEXT NOT NULL REFERENCES IssuedChecks(id),sectionKey TEXT NOT NULL REFERENCES SectionIdentities(sectionKey),sourceFacts TEXT NOT NULL,receipt TEXT NOT NULL,PRIMARY KEY(tokenId,sectionKey));
        CREATE TABLE Events(id TEXT PRIMARY KEY,sectionKey TEXT NOT NULL REFERENCES SectionIdentities(sectionKey),triggerOrdinal INTEGER NOT NULL REFERENCES IssuedChecks(ordinal),trigger TEXT NOT NULL,createdUtc TEXT NOT NULL,UNIQUE(sectionKey,triggerOrdinal));
        CREATE TABLE EventRecipients(eventId TEXT NOT NULL REFERENCES Events(id),subscriptionId TEXT NOT NULL REFERENCES Subscriptions(id),generation INTEGER NOT NULL,reason INTEGER NOT NULL,PRIMARY KEY(eventId,subscriptionId,generation));
        CREATE TABLE Outbox(eventId TEXT PRIMARY KEY REFERENCES Events(id),revision INTEGER NOT NULL CHECK(revision>0),status INTEGER NOT NULL,attempts INTEGER NOT NULL CHECK(attempts>=0));
        CREATE TABLE DeliveryAttempts(eventId TEXT NOT NULL REFERENCES Events(id),revision INTEGER NOT NULL,status INTEGER NOT NULL,attempts INTEGER NOT NULL,recordedUtc TEXT NOT NULL,PRIMARY KEY(eventId,revision));
        CREATE INDEX MembersSection ON Members(sectionKey);
        CREATE INDEX PendingOutbox ON Outbox(status);
        """;
}
