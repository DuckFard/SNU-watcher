using SnuWatcher.Application.Presentation.Alerts;

namespace SnuWatcher.Application.Monitoring.Hosting;

public sealed partial class MonitoringRuntimeSession : IAlertHistoryReader
{
    /// <summary>Bounded history reads use the same admitted, retained command lifetime as other presentation reads.</summary>
    public IAlertHistoryReader Alerts => this;

    Task<AlertHistoryPage> IAlertHistoryReader.ReadPageAsync(AlertHistoryCursor? after, int pageSize, CancellationToken cancellationToken)
    {
        ValidateHistoryPageSize(pageSize);
        var reader = RequireHistoryReader();
        return AdmitUiOperation(token => RunPresentationAsync(() => reader.ReadPageAsync(after, pageSize, token), token), cancellationToken);
    }

    Task<AlertHistoryItem?> IAlertHistoryReader.ReadDetailsAsync(Guid eventId, CancellationToken cancellationToken)
    {
        ValidateHistoryEvent(eventId);
        var reader = RequireHistoryReader();
        return AdmitUiOperation(token => RunPresentationAsync(() => reader.ReadDetailsAsync(eventId, token), token, allowNull: true), cancellationToken);
    }

    Task<AlertRecipientPage?> IAlertHistoryReader.ReadRecipientsAsync(Guid eventId, AlertRecipientCursor? after,
        int pageSize, CancellationToken cancellationToken)
    {
        ValidateHistoryEvent(eventId); ValidateHistoryPageSize(pageSize);
        if (after is not null && after.EventId != eventId) throw new ArgumentException("The recipient cursor belongs to another event.", nameof(after));
        var reader = RequireHistoryReader();
        return AdmitUiOperation(token => RunPresentationAsync(() => reader.ReadRecipientsAsync(eventId, after, pageSize, token), token, allowNull: true), cancellationToken);
    }

    Task<AlertAttemptPage?> IAlertHistoryReader.ReadAttemptsAsync(Guid eventId, AlertAttemptCursor? after,
        int pageSize, CancellationToken cancellationToken)
    {
        ValidateHistoryEvent(eventId); ValidateHistoryPageSize(pageSize);
        if (after is not null && after.EventId != eventId) throw new ArgumentException("The record cursor belongs to another event.", nameof(after));
        var reader = RequireHistoryReader();
        return AdmitUiOperation(token => RunPresentationAsync(() => reader.ReadAttemptsAsync(eventId, after, pageSize, token), token, allowNull: true), cancellationToken);
    }

    private IAlertHistoryReader RequireHistoryReader() => _presentationServices?.Alerts
        ?? throw new InvalidOperationException("Alert history is unavailable for this runtime.");

    private static void ValidateHistoryEvent(Guid eventId)
    {
        if (eventId == Guid.Empty) throw new ArgumentException("A saved event identity is required.", nameof(eventId));
    }

    private static void ValidateHistoryPageSize(int pageSize)
    {
        if (pageSize is < 1 or > AlertHistoryPage.MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize));
    }
}
