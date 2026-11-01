using Microsoft.Data.Sqlite;
using SnuWatcher.Core;

namespace SnuWatcher.Infrastructure.Persistence;

public sealed partial class SqliteWatchStore
{
    private const long ApplicationId = 0x534E5557;
    private const long SchemaVersion = 1;

    private void Initialize(SqliteConnection connection)
    {
        long version;
        using (var inspection = connection.BeginTransaction(deferred: true))
        {
            // The version, owner marker and schema inventory must describe one snapshot even
            // if another opener commits its initialization between these reads.
            version = ValidateSchema(connection, inspection);
            inspection.Commit();
        }
        if (version == 0)
        {
            Checkpoint(SqliteStoreWriteStage.SchemaValidated);
            using var transaction = BeginWrite(connection);
            // Another opener may have initialized the empty database while this connection waited
            // for its write reservation. Recheck under that reservation before executing any DDL.
            if (ValidateSchema(connection, transaction) == 0)
            {
                Run(connection, transaction, SchemaSql);
                Run(connection, transaction, "PRAGMA application_id=1397642583; PRAGMA user_version=1;");
                Checkpoint(SqliteStoreWriteStage.SchemaCreated);
            }
            transaction.Commit();
        }
        // Native identity has already been verified by the factory. Never mutate a future/foreign database.
        if (!string.Equals((string?)Scalar(connection, null, "PRAGMA journal_mode=WAL;"), "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SQLite could not enable the required journal mode.");
        Run(connection, null, "PRAGMA synchronous=FULL;");
    }

    private static long ValidateSchema(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var version = (long)Scalar(connection, transaction, "PRAGMA user_version;")!;
        var application = (long)Scalar(connection, transaction, "PRAGMA application_id;")!;
        var tables = (long)Scalar(connection, transaction, "SELECT count(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';")!;
        if (version > SchemaVersion || (version != 0 && version != SchemaVersion)
            || (application != 0 && application != ApplicationId)
            || (version == 0 && (application != 0 || tables != 0))
            || (version == SchemaVersion && application != ApplicationId))
            throw new InvalidOperationException("This database has an unsupported or unrecognized schema. It was not changed.");
        if (version == SchemaVersion && (long?)Scalar(connection, transaction, "SELECT count(*) FROM StoreMetadata WHERE id=1;") != 1)
            throw new InvalidOperationException("The watch database metadata is invalid.");
        return version;
    }

    private static void EnsureIdentity(SqliteConnection connection, SqliteTransaction transaction, SectionKey key) =>
        Run(connection, transaction,
            "INSERT OR IGNORE INTO SectionIdentities(sectionKey,year,semester,detail,course,section,subsubject) VALUES($key,$year,$semester,$detail,$course,$section,$subsubject);",
            ("$key", StoreWire.Key(key)), ("$year", key.Year), ("$semester", key.SemesterCode),
            ("$detail", key.DetailSemesterCode), ("$course", key.CourseCode), ("$section", key.Section), ("$subsubject", key.SubsubjectCode));

    private const string SchemaSql = """
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
