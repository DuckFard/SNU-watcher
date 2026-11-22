namespace SnuWatcher.Application.Presentation.Alerts;

/// <summary>Read-only durable history, independent of lossy runtime hints and native/source operations.</summary>
public interface IAlertHistoryReader
{
    /// <summary>Reads a short consistent global page, default 25/max 50; new page reads are not one frozen whole-history snapshot.</summary>
    Task<AlertHistoryPage> ReadPageAsync(AlertHistoryCursor? after = null, int pageSize = 25, CancellationToken cancellationToken = default);
    /// <summary>Reads one scalar event/current durable-delivery projection; null means the event does not exist.</summary>
    Task<AlertHistoryItem?> ReadDetailsAsync(Guid eventId, CancellationToken cancellationToken = default);
    /// <summary>Reads original recipient associations with optional current labels; null means no event. No eager membership array.</summary>
    Task<AlertRecipientPage?> ReadRecipientsAsync(Guid eventId, AlertRecipientCursor? after = null, int pageSize = 25,
        CancellationToken cancellationToken = default);
    /// <summary>Reads associated whole-batch attempts plus event-result revision buckets, not distinct submissions or chronological records.</summary>
    Task<AlertAttemptPage?> ReadAttemptsAsync(Guid eventId, AlertAttemptCursor? after = null, int pageSize = 25,
        CancellationToken cancellationToken = default);
}

/// <summary>Fixed storage-boundary failure. No database path, SQL, source payload or exception detail crosses the port.</summary>
public sealed class AlertHistoryReadException : Exception
{
    public AlertHistoryReadException() : base("Alert history could not be read. Try again after the saved state is available.") { }
    public string DiagnosticCode => "alert_history_unavailable";
}
