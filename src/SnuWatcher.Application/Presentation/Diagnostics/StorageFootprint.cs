namespace SnuWatcher.Application.Presentation.Diagnostics;

/// <summary>Missing sidecars are ordinary; unavailable metadata must never be reported as zero bytes.</summary>
public enum DiagnosticFileState { Available, Missing, Unavailable }

/// <summary>File length metadata only. No filename, content or database connection is retained.</summary>
public sealed record DiagnosticFileSize
{
    public DiagnosticFileSize(DiagnosticFileState state, long? bytes = null)
    {
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if ((state == DiagnosticFileState.Available) != bytes.HasValue) throw new ArgumentException("Only available metadata has a byte length.");
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        State = state; Bytes = bytes;
    }
    public DiagnosticFileState State { get; }
    public long? Bytes { get; }
}

/// <summary>Three independently sampled file lengths. The warning is preview policy, never retention or a stop condition.</summary>
public sealed record StorageFootprint
{
    public const long WarningThresholdBytes = 256L * 1024 * 1024;
    public StorageFootprint(DiagnosticFileSize database, DiagnosticFileSize wal, DiagnosticFileSize shm)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(wal); ArgumentNullException.ThrowIfNull(shm);
        Database = database; Wal = wal; Shm = shm;
    }
    public DiagnosticFileSize Database { get; }
    public DiagnosticFileSize Wal { get; }
    public DiagnosticFileSize Shm { get; }
    public bool IsComplete => Database.State != DiagnosticFileState.Unavailable
        && Wal.State != DiagnosticFileState.Unavailable && Shm.State != DiagnosticFileState.Unavailable;
    public long? TotalBytes
    {
        get
        {
            if (!IsComplete) return null;
            try { return checked((Database.Bytes ?? 0) + (Wal.Bytes ?? 0) + (Shm.Bytes ?? 0)); }
            catch (OverflowException) { return null; }
        }
    }
    public bool HasSizeWarning => Database.Bytes >= WarningThresholdBytes || Wal.Bytes >= WarningThresholdBytes
        || Shm.Bytes >= WarningThresholdBytes || TotalBytes >= WarningThresholdBytes;
}

/// <summary>Reads only the three fixed owned-profile file lengths, without SQL or file contents.</summary>
public interface IStorageDiagnosticsReader
{
    Task<StorageFootprint> ReadAsync(CancellationToken cancellationToken = default);
}
