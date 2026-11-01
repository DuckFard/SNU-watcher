using Microsoft.Data.Sqlite;
using SnuWatcher.Core;

namespace SnuWatcher.Infrastructure.Persistence;

public sealed partial class SqliteWatchStore
{
    private const long ApplicationId = 0x534E5557;
    private const long SchemaVersion = 5;

    private void Initialize(SqliteConnection connection, CancellationToken cancellationToken)
    {
        long version;
        using (var inspection = connection.BeginTransaction(deferred: true))
        {
            // The version, owner marker and schema inventory must describe one snapshot even
            // if another opener commits its initialization between these reads.
            version = ValidateSchema(connection, inspection);
            inspection.Commit();
        }
        if (version < SchemaVersion)
        {
            Checkpoint(SqliteStoreWriteStage.SchemaValidated);
            using var transaction = BeginWrite(connection);
            // Another opener may have initialized the empty database while this connection waited
            // for its write reservation. Recheck under that reservation before executing any DDL.
            var reservedVersion = ValidateSchema(connection, transaction);
            if (reservedVersion == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Run(connection, transaction, SchemaSql);
                Run(connection, transaction, LegacyImportSchemaSql);
                Run(connection, transaction, BatchSchemaSql);
                Run(connection, transaction, UiSchemaSql);
                Run(connection, transaction, HistorySchemaSql);
                Run(connection, transaction, "PRAGMA application_id=1397642583; PRAGMA user_version=5;");
                Checkpoint(SqliteStoreWriteStage.SchemaCreated);
            }
            else if (reservedVersion is 1 or 2 or 3 or 4)
            {
                // The reservation blocks other writers, but the native backup reads from its own
                // anchored readonly connection. Backing up the writing handle would deadlock.
                MigrationBackupPath = BackupRecognizedVersion(reservedVersion, cancellationToken);
                Checkpoint(SqliteStoreWriteStage.MigrationBackup);
                cancellationToken.ThrowIfCancellationRequested();
                if (reservedVersion == 1) Run(connection, transaction, LegacyImportSchemaSql);
                if (reservedVersion < 3) Run(connection, transaction, BatchSchemaSql);
                if (reservedVersion < 4) Run(connection, transaction, UiSchemaSql);
                Run(connection, transaction, HistorySchemaSql);
                Checkpoint(SqliteStoreWriteStage.MigrationDdl);
                cancellationToken.ThrowIfCancellationRequested();
                Run(connection, transaction, "PRAGMA user_version=5;");
                Checkpoint(SqliteStoreWriteStage.MigrationVersion);
            }
            cancellationToken.ThrowIfCancellationRequested();
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
        var objects = (long)Scalar(connection, transaction, "SELECT count(*) FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*';")!;
        if (version > SchemaVersion || version < 0
            || (application != 0 && application != ApplicationId)
            || (version == 0 && (application != 0 || objects != 0))
            || (version > 0 && application != ApplicationId))
            throw new InvalidOperationException("This database has an unsupported or unrecognized schema. It was not changed.");
        if (version > 0)
        {
            ValidateOwnedObjects(connection, transaction, version);
            if ((long?)Scalar(connection, transaction, "SELECT count(*) FROM StoreMetadata WHERE id=1;") != 1)
                throw new InvalidOperationException("The watch database metadata is invalid.");
        }
        return version;
    }

    private static void ValidateOwnedObjects(SqliteConnection connection, SqliteTransaction? transaction, long version)
    {
        var definitions = KnownSchemaDefinitions(version);
        var expected = definitions.Keys.ToHashSet(StringComparer.Ordinal);
        using var command = Command(connection, transaction,
            "SELECT name,type,sql FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*';");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            var type = reader.GetString(1);
            if (!expected.Remove(name) || type != (definitions[name].StartsWith("CREATE TABLE ", StringComparison.Ordinal) ? "table" : "index") || reader.IsDBNull(2)
                || !string.Equals(definitions[name], CanonicalDdl(reader.GetString(2)), StringComparison.Ordinal))
                throw new InvalidOperationException("The watch database schema differs from its supported producer layout.");
        }
        if (expected.Count != 0) throw new InvalidOperationException("The watch database schema is incomplete.");
    }

    private static Dictionary<string, string> KnownSchemaDefinitions(long version)
    {
        // Recognized producer versions have fixed DDL and no quoted semicolon literals. Compare
        // complete definitions, including columns, constraints, foreign keys and index expressions.
        // Do not remove interior whitespace or change quoted identifiers/literals when recognizing it.
        var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
        var schema = SchemaSql + (version >= 2 ? LegacyImportSchemaSql : "") + (version >= 3 ? BatchSchemaSql : "")
            + (version >= 4 ? UiSchemaSql : "") + (version >= 5 ? HistorySchemaSql : "");
        foreach (var statement in schema.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var prefix = statement.StartsWith("CREATE TABLE ", StringComparison.Ordinal) ? "CREATE TABLE "
                : statement.StartsWith("CREATE UNIQUE INDEX ", StringComparison.Ordinal) ? "CREATE UNIQUE INDEX "
                : statement.StartsWith("CREATE INDEX ", StringComparison.Ordinal) ? "CREATE INDEX " : null;
            if (prefix is null) continue;
            var remaining = statement[prefix.Length..];
            var end = remaining.IndexOfAny([' ', '(']);
            definitions.Add(remaining[..end], CanonicalDdl(statement));
        }
        return definitions;
    }

    private static string CanonicalDdl(string sql)
    {
        // Git checkout line endings survive C# raw literals. Normalize only unquoted CRLF;
        // all token spacing and every quoted identifier/literal character remain exact.
        var value = new System.Text.StringBuilder(sql.Length);
        var quote = '\0';
        for (var index = 0; index < sql.Length; index++)
        {
            var character = sql[index];
            if (quote != '\0')
            {
                value.Append(character);
                if (character == quote)
                {
                    if (index + 1 < sql.Length && sql[index + 1] == quote) value.Append(sql[++index]);
                    else quote = '\0';
                }
            }
            else if (character is '\'' or '"' or '`' or '[')
            {
                quote = character == '[' ? ']' : character;
                value.Append(character);
            }
            else if (character == '\r' && index + 1 < sql.Length && sql[index + 1] == '\n')
            {
                value.Append('\n');
                index++;
            }
            else value.Append(character);
        }
        return value.ToString().Trim().TrimEnd(';').Trim();
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

    private const string LegacyImportSchemaSql = """
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
