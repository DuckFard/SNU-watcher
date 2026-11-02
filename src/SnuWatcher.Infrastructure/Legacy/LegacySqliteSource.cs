using System.Globalization;
using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Migration;
using SnuWatcher.Infrastructure.Persistence;

namespace SnuWatcher.Infrastructure.Legacy;

internal static class LegacySqliteSource
{
    private const string StateRows = """
        SELECT
            CASE WHEN typeof(storage_key)='text' AND instr(storage_key,char(0))=0 AND length(CAST(storage_key AS BLOB))<=170 THEN storage_key END,
            CASE WHEN typeof(year)='integer' AND year BETWEEN 2000 AND 2200 THEN year END,
            CASE WHEN typeof(semester_code)='text' AND instr(semester_code,char(0))=0 AND length(CAST(semester_code AS BLOB))<=80 THEN semester_code END,
            CASE WHEN typeof(subject_code)='text' AND instr(subject_code,char(0))=0 AND length(CAST(subject_code AS BLOB))<=80 THEN subject_code END,
            CASE WHEN typeof(section)='text' AND instr(section,char(0))=0 AND length(CAST(section AS BLOB))<=3 THEN section END,
            CASE WHEN typeof(availability)='text' AND instr(availability,char(0))=0 AND length(CAST(availability AS BLOB))<=16 THEN availability END,
            CASE WHEN typeof(name)='text' AND instr(name,char(0))=0 AND length(name)<=512 AND length(CAST(name AS BLOB))<=2048 THEN name END,
            registered IS NULL OR (typeof(registered)='integer' AND registered BETWEEN 0 AND 2147483647),
            CASE WHEN typeof(registered)='integer' AND registered BETWEEN 0 AND 2147483647 THEN registered END,
            capacity IS NULL OR (typeof(capacity)='integer' AND capacity BETWEEN 0 AND 2147483647),
            CASE WHEN typeof(capacity)='integer' AND capacity BETWEEN 0 AND 2147483647 THEN capacity END,
            CASE WHEN typeof(observed_at)='text' AND instr(observed_at,char(0))=0 AND length(observed_at)<=128 AND length(CAST(observed_at AS BLOB))<=512 THEN observed_at END
        FROM course_state ORDER BY storage_key LIMIT 2001;
        """;

    internal static List<LegacyDefinition> Read(string path, LegacyDiagnostics diagnostics, Action? snapshotAnchored, Action? afterSidecarsPinned,
        CancellationToken cancellationToken)
    {
        var source = LegacySourceFiles.ValidateDatabase(path);
        cancellationToken.ThrowIfCancellationRequested();
        using var sidecars = PinWalPrerequisites(source);
        afterSidecarsPinned?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = new SqliteConnectionFactory(source).Open(SqliteOpenMode.ReadOnly);
        using (var protection = connection.CreateCommand())
        {
            protection.CommandText = "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF;";
            protection.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction(deferred: true);
        LegacySqliteSchema.RequireRecognized(connection, transaction);
        snapshotAnchored?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        _ = LegacySourceFiles.ValidateDatabase(source);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = StateRows;
        var definitions = new List<LegacyDefinition>();
        using (var reader = command.ExecuteReader())
        {
            var row = 0;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++row > 2000) throw new LegacySourceException("legacy_state_rows_exceeded");
                if (!TryIdentity(reader, out var identity))
                {
                    diagnostics.Add(LegacySourceKind.StateDatabase, "legacy_state_row_invalid", row);
                    continue;
                }
                var history = ReadHistory(reader, out var incomplete);
                if (incomplete) diagnostics.Add(LegacySourceKind.StateDatabase, "legacy_state_history_incomplete", row);
                var name = history?.Name;
                var label = name is { Length: > 0 and <= 120 } && !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl)
                    ? name : LegacyTomlSource.DisplayLabel(identity!);
                definitions.Add(new(identity!, label, false, history));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        _ = LegacySourceFiles.ValidateDatabase(source);
        transaction.Commit();
        return definitions;
    }

    private static WalSidecars? PinWalPrerequisites(string source)
    {
        // SQLite readonly mode may create missing WAL sidecars when its directory is writable.
        // Require an existing live-WAL profile instead of repairing or recreating that profile.
        using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[20];
        file.ReadExactly(header);
        if (header[18] != 2 && header[19] != 2) return null;
        FileStream? wal = null;
        try
        {
            wal = LegacySourceFiles.PinExistingSidecar(source + "-wal");
            return new WalSidecars(wal, LegacySourceFiles.PinExistingSidecar(source + "-shm"));
        }
        catch (LegacySourceException)
        {
            wal?.Dispose();
            throw new LegacySourceException("legacy_database_wal_unavailable");
        }
    }

    private sealed class WalSidecars(FileStream wal, FileStream shm) : IDisposable
    {
        public void Dispose() { try { shm.Dispose(); } finally { wal.Dispose(); } }
    }

    private static bool TryIdentity(SqliteDataReader reader, out LegacyCourseIdentity? identity)
    {
        identity = null;
        if (Enumerable.Range(0, 5).Any(reader.IsDBNull)) return false;
        try
        {
            identity = new((int)reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
            return string.Equals(reader.GetString(0), identity.StorageKey, StringComparison.Ordinal);
        }
        catch (ArgumentException) { return false; }
    }

    private static LegacyHistoricalState? ReadHistory(SqliteDataReader reader, out bool incomplete)
    {
        incomplete = true;
        if (reader.IsDBNull(5) || reader.IsDBNull(6) || reader.IsDBNull(11)) return null;
        var availability = reader.GetString(5) switch
        {
            "unknown" => LegacyAvailability.Unknown,
            "full" => LegacyAvailability.Full,
            "available" => LegacyAvailability.Available,
            _ => (LegacyAvailability?)null,
        };
        if (availability is null) return null;
        var original = reader.GetString(11);
        var instant = ReadTime(original, out var timeValid);
        try
        {
            var history = new LegacyHistoricalState(reader.GetString(6), reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(10) ? null : reader.GetInt32(10), availability.Value, original, instant);
            incomplete = !reader.GetBoolean(7) || !reader.GetBoolean(9) || !timeValid;
            return history;
        }
        catch (ArgumentException) { return null; }
    }

    private static DateTimeOffset? ReadTime(string text, out bool valid)
    {
        string[] offsetFormats = ["yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
            "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"];
        if (DateTimeOffset.TryParseExact(text, offsetFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant))
        { valid = true; return instant; }
        string[] naiveFormats = ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"];
        valid = DateTime.TryParseExact(text, naiveFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        return null;
    }
}
