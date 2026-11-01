using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace SnuWatcher.Infrastructure.Persistence;

public sealed partial class SqliteWatchStore
{
    /// <summary>The completed pre-migration snapshot, if this opener performed a V1 upgrade. Never automatically restored.</summary>
    public string? MigrationBackupPath { get; private set; }

    private string BackupVersionOne(CancellationToken cancellationToken)
    {
        var budget = Stopwatch.StartNew();
        Checkpoint(SqliteStoreWriteStage.MigrationBackupStarted);
        EnsureBackupBudget(budget, cancellationToken);
        var path = _factory.DatabasePath + ".before-v2-" + Guid.NewGuid().ToString("N") + ".sqlite3";
        var complete = false;
        try
        {
            // CreateNew prevents replacement. Keeping this handle open without delete sharing also
            // prevents the reserved file being renamed between creation and SQLite opening it.
            using var reservation = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite, 4096, FileOptions.WriteThrough);
            ProtectBackup(path);
            EnsureBackupBudget(budget, cancellationToken);
            using (var source = _factory.Open(SqliteOpenMode.ReadOnly))
            using (var snapshot = source.BeginTransaction(deferred: true))
            {
                Run(source, snapshot, "PRAGMA query_only=ON;");
                if (ValidateSchema(source, snapshot) != 1)
                    throw new InvalidOperationException("The migration backup source is no longer schema V1.");
                EnsureBackupBudget(budget, cancellationToken);
                var expected = BackupRowCounts(source, snapshot, budget, cancellationToken);
                using (var destination = new SqliteConnectionFactory(path).Open(SqliteOpenMode.ReadWrite))
                {
                    Run(destination, null, "PRAGMA synchronous=FULL;");
                    CopyBackupPages(source, destination, budget, cancellationToken);
                    // A completed backup is standalone, even when the source had uncheckpointed WAL data.
                    if (!string.Equals((string?)Scalar(destination, null, "PRAGMA journal_mode=DELETE;"), "delete", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The migration backup could not become a standalone database.");
                }
                reservation.Flush(flushToDisk: true);
                EnsureBackupBudget(budget, cancellationToken);
                using var verify = new SqliteConnectionFactory(path).Open(SqliteOpenMode.ReadOnly);
                using var verification = verify.BeginTransaction(deferred: true);
                if (ValidateSchema(verify, verification) != 1
                    || !string.Equals((string?)Scalar(verify, verification, "PRAGMA integrity_check;"), "ok", StringComparison.Ordinal)
                    || !expected.SequenceEqual(BackupRowCounts(verify, verification, budget, cancellationToken)))
                    throw new InvalidOperationException("The completed migration backup could not be verified.");
                verification.Commit();
                snapshot.Commit();
            }
            reservation.Flush(flushToDisk: true);
            complete = true;
            EnsureBackupBudget(budget, cancellationToken);
            return path;
        }
        catch (Exception original)
        {
            if (!complete)
            {
                try { DeleteIncompleteBackup(path); }
                catch (Exception cleanup)
                { throw new AggregateException("Migration backup failed and its incomplete artifact could not be removed.", original, cleanup); }
            }
            throw;
        }
    }

    private static void CopyBackupPages(SqliteConnection source, SqliteConnection destination, Stopwatch budget, CancellationToken cancellationToken)
    {
        var sourceHandle = source.Handle ?? throw new InvalidOperationException("The backup source is not open.");
        var destinationHandle = destination.Handle ?? throw new InvalidOperationException("The backup destination is not open.");
        if (raw.sqlite3_busy_timeout(sourceHandle, 100) != raw.SQLITE_OK || raw.sqlite3_busy_timeout(destinationHandle, 100) != raw.SQLITE_OK)
            throw new InvalidOperationException("The backup lock deadline could not be configured.");
        using var backup = raw.sqlite3_backup_init(destinationHandle, "main", sourceHandle, "main");
        if (backup is null || backup.IsInvalid) throw new InvalidOperationException("SQLite could not initialize the migration backup.");
        var finish = raw.SQLITE_OK;
        try
        {
            while (true)
            {
                EnsureBackupBudget(budget, cancellationToken);
                var result = raw.sqlite3_backup_step(backup, 64);
                if (raw.sqlite3_backup_pagecount(backup) > 262144)
                    throw new IOException("The migration backup exceeds its page budget.");
                if (result == raw.SQLITE_DONE) break;
                if (result == raw.SQLITE_OK) continue;
                if (result is raw.SQLITE_BUSY or raw.SQLITE_LOCKED)
                {
                    if (cancellationToken.WaitHandle.WaitOne(10)) cancellationToken.ThrowIfCancellationRequested();
                    continue;
                }
                throw new IOException($"SQLite migration backup failed with code {result}.");
            }
        }
        finally { finish = raw.sqlite3_backup_finish(backup); }
        if (finish != raw.SQLITE_OK) throw new IOException($"SQLite migration backup finalization failed with code {finish}.");
    }

    private static long[] BackupRowCounts(SqliteConnection connection, SqliteTransaction transaction,
        Stopwatch budget, CancellationToken cancellationToken) => BackupCountQueries.Select(sql =>
        {
            EnsureBackupBudget(budget, cancellationToken);
            var count = (long)Scalar(connection, transaction, sql)!;
            EnsureBackupBudget(budget, cancellationToken);
            return count;
        }).ToArray();

    private static void EnsureBackupBudget(Stopwatch budget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (budget.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("The migration backup exceeded its deadline.");
    }

    private static readonly string[] BackupCountQueries =
    [
        "SELECT count(*) FROM StoreMetadata;", "SELECT count(*) FROM SectionIdentities;", "SELECT count(*) FROM IssuedChecks;",
        "SELECT count(*) FROM IssuedScopes;", "SELECT count(*) FROM Subscriptions;", "SELECT count(*) FROM Members;",
        "SELECT count(*) FROM SectionState;", "SELECT count(*) FROM Completions;", "SELECT count(*) FROM Events;",
        "SELECT count(*) FROM EventRecipients;", "SELECT count(*) FROM Outbox;", "SELECT count(*) FROM DeliveryAttempts;",
    ];

    private void DeleteIncompleteBackup(string path)
    {
        var source = Path.GetFullPath(_factory.DatabasePath);
        var target = Path.GetFullPath(path);
        var prefix = Path.GetFileName(source) + ".before-v2-";
        var name = Path.GetFileName(target);
        if (!string.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".sqlite3", StringComparison.Ordinal)
            || !Guid.TryParseExact(name[prefix.Length..^".sqlite3".Length], "N", out _))
            throw new InvalidOperationException("Refusing cleanup outside this migration's backup path.");
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var owned = target + suffix;
            if (File.Exists(owned))
            {
                if ((File.GetAttributes(owned) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Refusing cleanup of a reparse backup artifact.");
                File.Delete(owned);
            }
        }
    }

    private static void ProtectBackup(string path)
    {
        if (OperatingSystem.IsWindows()) ProtectWindowsBackup(path);
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectWindowsBackup(string path)
    {
        using var owner = WindowsIdentity.GetCurrent();
        var user = owner.User ?? throw new InvalidOperationException("The backup owner identity is unavailable.");
        var access = new FileSecurity();
        access.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var identity in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            access.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(access);
    }
}
