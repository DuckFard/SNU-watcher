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
/// Frozen producer V2, independent of the current store initializer. The historical seed stays only
/// in committed WAL while its writer remains open, so a main-file-only backup cannot pass these tests.
/// </summary>
internal sealed class StoreBatchV2Fixture : IDisposable
{
    private const string Prefix = "SnuWatcher-v3-schema-";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _writer;
    internal const long AppId = 1397642583;
    internal static readonly Guid SubscriptionId = Guid.ParseExact("11111111111111111111111111111111", "N");
    internal static readonly Guid TokenId = Guid.ParseExact("22222222222222222222222222222222", "N");
    internal static readonly Guid EventId = Guid.ParseExact("33333333333333333333333333333333", "N");
    internal static readonly SectionKey Key = new(2026, "U000200002", "U000300002", "F32.102", "012", "0");
    internal const string LegacyKey = "python-watcher-v0:2026:U000200002:F32.102:012";
    internal const string SeedKey = "[2026,\"U000200002\",\"U000300002\",\"F32.102\",\"012\",\"0\"]";
    internal const string SeedTime = "2026-11-01T00:00:00.0000000+00:00";
    internal const string Provenance = """{"version":1,"historicalOnly":true,"name":"Old exported statistics","registered":100,"capacity":120,"availability":0,"originalObservedText":"2026-10-31T12:00:00+09:00","observedUtc":"2026-10-31T03:00:00.0000000+00:00"}""";

    internal StoreBatchV2Fixture(bool createHistoricalSchema = true)
    {
        Directory.CreateDirectory(_directory);
        Factory = new SqliteConnectionFactory(DatabasePath);
        _writer = Factory.Open();
        try
        {
            if (!createHistoricalSchema) return;
            Execute("PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
            using (var transaction = _writer.BeginTransaction())
            {
                Execute(FrozenV2BaseSchema, transaction);
                Execute(FrozenV2LegacySchema, transaction);
                Execute("PRAGMA application_id=1397642583; PRAGMA user_version=2;", transaction);
                transaction.Commit();
            }
            Execute("PRAGMA wal_checkpoint(TRUNCATE);");
            var beforeSeed = DatabaseHash();
            Seed();
            Assert.Equal(beforeSeed, DatabaseHash());
            Assert.True(new FileInfo(DatabasePath + "-wal").Length > 0, "Historical rows must be committed only in WAL.");
        }
        catch
        {
            _writer.Dispose();
            DeleteDirectory();
            throw;
        }
    }

    internal string DatabasePath => Path.Combine(_directory, "producer-v2.sqlite3");
    internal SqliteConnectionFactory Factory { get; }
    internal long Version => Convert.ToInt64(Scalar(_writer, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
    internal string[] Backups() => AllBackups(DatabasePath);
    internal static string[] AllBackups(string databasePath) => Directory.GetFiles(Path.GetDirectoryName(databasePath)!,
        Path.GetFileName(databasePath) + ".before-*.sqlite3");

    internal byte[] DatabaseHash()
    {
        using var stream = new FileStream(DatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(stream);
    }

    internal void SetHeader(long version, long applicationId) => Execute(
        "PRAGMA user_version=" + version.ToString(CultureInfo.InvariantCulture)
        + "; PRAGMA application_id=" + applicationId.ToString(CultureInfo.InvariantCulture) + ";");
    internal void Alter(string sql) => Execute(sql);
    internal string LogicalSnapshot() => LogicalSnapshot(_writer);
    internal string HistoricalRows() => HistoricalRows(_writer);

    internal static string LogicalSnapshot(SqliteConnection connection)
    {
        var snapshot = new List<object?> { Scalar(connection, "PRAGMA user_version;"), Scalar(connection, "PRAGMA application_id;") };
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT type,name,sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name;";
            using var reader = command.ExecuteReader();
            List<object?[]> schema = [];
            while (reader.Read()) schema.Add(Row(reader));
            snapshot.Add(schema);
        }
        snapshot.Add(HistoricalRows(connection));
        return JsonSerializer.Serialize(snapshot);
    }

    internal static string HistoricalRows(SqliteConnection connection, bool includeLegacy = true)
    {
        // Historical, fixed allowlist: new producer tables must not redefine the migration oracle.
        string[] tables = ["StoreMetadata", "SectionIdentities", "IssuedChecks", "IssuedScopes", "Subscriptions", "Members",
            "SectionState", "Completions", "Events", "EventRecipients", "Outbox", "DeliveryAttempts"];
        List<object> snapshot = [];
        foreach (var table in includeLegacy ? tables.Append("LegacyImports") : tables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM " + table + " ORDER BY rowid;";
            using var reader = command.ExecuteReader();
            List<object?[]> rows = [];
            while (reader.Read()) rows.Add(Row(reader));
            snapshot.Add(new { table, rows });
        }
        return JsonSerializer.Serialize(snapshot);
    }

    internal static void AssertStandaloneBackup(string path, long originalVersion)
    {
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(path + "-shm"));
        Assert.False(File.Exists(path + "-journal"));
        using (var stream = File.OpenRead(path))
        {
            var header = new byte[100];
            stream.ReadExactly(header);
            Assert.Equal("SQLite format 3\0", System.Text.Encoding.ASCII.GetString(header, 0, 16));
            Assert.Equal(originalVersion, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(60, 4)));
            Assert.Equal(AppId, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(68, 4)));
            Assert.Equal((byte)1, header[18]);
            Assert.Equal((byte)1, header[19]);
        }
        using var verify = new SqliteConnectionFactory(path).Open(SqliteOpenMode.ReadOnly);
        Assert.Equal("ok", Scalar(verify, "PRAGMA integrity_check;"));
        Assert.Null(Scalar(verify, "PRAGMA foreign_key_check;"));
    }

    private void Seed()
    {
        // Frozen wire literals, not current StoreWire or production DTO serialization.
        const string subscription = """{"id":"11111111111111111111111111111111","revision":5,"generation":2,"kind":0,"label":"Frozen V2 \uC11C\uC6B8","status":0,"initial":true,"expiry":null,"created":"2026-11-01T00:00:00.0000000+00:00"}""";
        var quotedKey = JsonSerializer.Serialize(SeedKey);
        var createFacts = "{\"version\":1,\"id\":\"11111111111111111111111111111111\",\"label\":\"Frozen V2 \\uC11C\\uC6B8\",\"kind\":0,\"sections\":[" + quotedKey + "],\"initial\":true,\"expiry\":null,\"created\":\"" + SeedTime + "\"}";
        var section = "{\"key\":" + quotedKey + ",\"version\":4,\"ordinal\":7,\"outcome\":0,\"attempted\":\"" + SeedTime
            + "\",\"success\":\"" + SeedTime + "\",\"baseline\":{\"registered\":119,\"capacity\":120,\"meaning\":\"enrolled-over-quota\",\"ordinal\":7,\"observed\":\"" + SeedTime + "\"}}";
        var trigger = "{\"key\":" + quotedKey + ",\"seats\":1,\"previous\":0,\"ordinal\":7,\"observed\":\"" + SeedTime
            + "\",\"recipients\":[{\"id\":\"11111111111111111111111111111111\",\"generation\":2,\"reason\":1}]}";
        var sourceFacts = "{\"version\":1,\"token\":\"22222222222222222222222222222222\",\"ordinal\":7,\"key\":" + quotedKey
            + ",\"observed\":\"" + SeedTime + "\",\"kind\":0,\"registered\":119,\"capacity\":120,\"meaning\":\"enrolled-over-quota\",\"outcome\":0,\"diagnostic\":null}";
        var receipt = "{\"token\":\"22222222222222222222222222222222\",\"ordinal\":7,\"key\":" + quotedKey
            + ",\"disposition\":0,\"version\":4,\"eventId\":\"33333333333333333333333333333333\"}";
        using var transaction = _writer.BeginTransaction();
        Execute("UPDATE StoreMetadata SET issuedOrdinal=7,catalogRevision=3 WHERE id=1;", transaction);
        Execute("INSERT INTO SectionIdentities VALUES($key,2026,'U000200002','U000300002','F32.102','012','0');", transaction, ("$key", SeedKey));
        Execute("INSERT INTO IssuedChecks VALUES($token,7,$time); INSERT INTO IssuedScopes VALUES($token,$key);", transaction,
            ("$token", TokenId.ToString("N")), ("$time", SeedTime), ("$key", SeedKey));
        Execute("INSERT INTO Subscriptions VALUES($subscription,$facts,$snapshot); INSERT INTO Members VALUES($subscription,2,$key,0,7);", transaction,
            ("$subscription", SubscriptionId.ToString("N")), ("$facts", createFacts), ("$snapshot", subscription), ("$key", SeedKey));
        Execute("INSERT INTO SectionState VALUES($key,$snapshot); INSERT INTO Completions VALUES($token,$key,$facts,$receipt);", transaction,
            ("$key", SeedKey), ("$snapshot", section), ("$token", TokenId.ToString("N")), ("$facts", sourceFacts), ("$receipt", receipt));
        Execute("INSERT INTO Events VALUES($event,$key,7,$trigger,$time); INSERT INTO EventRecipients VALUES($event,$subscription,2,1); INSERT INTO Outbox VALUES($event,1,0,0);", transaction,
            ("$event", EventId.ToString("N")), ("$key", SeedKey), ("$trigger", trigger), ("$time", SeedTime), ("$subscription", SubscriptionId.ToString("N")));
        Execute("INSERT INTO LegacyImports VALUES($legacy,$key,$subscription,$time,$provenance);", transaction,
            ("$legacy", LegacyKey), ("$key", SeedKey), ("$subscription", SubscriptionId.ToString("N")), ("$time", SeedTime), ("$provenance", Provenance));
        transaction.Commit();
    }

    private static object?[] Row(SqliteDataReader reader)
    {
        var values = new object[reader.FieldCount];
        reader.GetValues(values);
        return values.Select(value => value is DBNull ? null : value).ToArray();
    }

    internal static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private void Execute(string sql, SqliteTransaction? transaction = null, params (string Name, object Value)[] parameters)
    {
        using var command = _writer.CreateCommand();
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
            throw new InvalidOperationException("Refusing cleanup outside the V3 fixture's unique temporary directory.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }

    // Frozen verbatim from 0f13004c22ccd30f11b098dee013df71aeee1489: StoreSchema.SchemaSql.
    private const string FrozenV2BaseSchema = """
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

    // Frozen verbatim from the same producer: StoreSchema.LegacyImportSchemaSql.
    private const string FrozenV2LegacySchema = """
        CREATE TABLE LegacyImports(
            legacyKey TEXT NOT NULL,
            targetKey TEXT NOT NULL REFERENCES SectionIdentities(sectionKey),
            subscriptionId TEXT NOT NULL REFERENCES Subscriptions(id),
            importedUtc TEXT NOT NULL,
            provenance TEXT,
            PRIMARY KEY(legacyKey,targetKey)
        );
        """;
}
