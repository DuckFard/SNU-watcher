using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Application.Presentation.Reads;

namespace SnuWatcher.Infrastructure.Persistence;

public sealed partial class SqliteWatchStore : IAlertHistoryReader
{
    Task<AlertHistoryPage> IAlertHistoryReader.ReadPageAsync(AlertHistoryCursor? after, int pageSize, CancellationToken cancellationToken)
    {
        HistoryPageSize(pageSize);
        return HistoryReadAsync((connection, transaction, stamp) =>
        {
            var high = after?.HighWaterOrdinal ?? stamp.IssuedCheckOrdinal;
            if (high > stamp.IssuedCheckOrdinal) throw new InvalidDataException("History cursor exceeds saved issued state.");
            using var command = Command(connection, transaction,
                StoreHistoryQueries.Summary(after is null ? StoreHistoryQueries.FirstIds : StoreHistoryQueries.LaterIds, true),
                ("$size", pageSize), ("$take", pageSize + 1));
            if (after is null) command.Parameters.AddWithValue("$high", high);
            else
            {
                command.Parameters.AddWithValue("$ordinal", after.AfterOrdinal);
                command.Parameters.AddWithValue("$id", after.AfterEventId.ToString("N"));
            }
            List<AlertHistoryItem> items = [];
            var more = false;
            using var rows = command.ExecuteReader();
            while (rows.Read()) { items.Add(ReadHistoryItem(rows)); more = rows.GetBoolean(18); }
            var cursor = more ? new AlertHistoryCursor(high, items[^1].Trigger.Ordinal, items[^1].EventId) : null;
            return new AlertHistoryPage(stamp, high, items, more, cursor);
        }, cancellationToken);
    }

    Task<AlertHistoryItem?> IAlertHistoryReader.ReadDetailsAsync(Guid eventId, CancellationToken cancellationToken)
    {
        HistoryEventId(eventId);
        return HistoryReadAsync<AlertHistoryItem?>((connection, transaction, _) =>
        {
            using var command = Command(connection, transaction, StoreHistoryQueries.Summary(StoreHistoryQueries.DetailId, false),
                ("$event", eventId.ToString("N")), ("$size", 1));
            using var rows = command.ExecuteReader();
            return rows.Read() ? ReadHistoryItem(rows) : null;
        }, cancellationToken);
    }

    private async Task<T> HistoryReadAsync<T>(Func<SqliteConnection, SqliteTransaction, UiReadStamp, T> read, CancellationToken token)
    {
        try
        {
            return await _lane.ExecuteAsync(connection =>
            {
                Run(connection, null, "PRAGMA query_only=ON;");
                using var transaction = connection.BeginTransaction(deferred: true);
                var stamp = ReadUiStamp(connection, transaction);
                // Both maxima use existing ordinal indexes. Corrupt counters must not masquerade as empty history.
                var latestCheck = (long?)Scalar(connection, transaction, "SELECT max(ordinal) FROM IssuedChecks;") ?? 0;
                var latestEvent = (long?)Scalar(connection, transaction, "SELECT max(triggerOrdinal) FROM Events;") ?? 0;
                if (latestCheck > stamp.IssuedCheckOrdinal || latestEvent > stamp.IssuedCheckOrdinal)
                    throw new InvalidDataException("History exceeds saved issued state.");
                var result = read(connection, transaction, stamp);
                transaction.Commit();
                return result;
            }, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ObjectDisposedException && error is IOException or InvalidDataException or System.Text.Json.JsonException
            or FormatException or OverflowException or InvalidCastException or ArgumentException or InvalidOperationException
            or KeyNotFoundException or SqliteException)
        { throw new AlertHistoryReadException(); }
    }

    private static void HistoryPageSize(int value)
    { if (value is < 1 or > AlertHistoryPage.MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(value)); }
    private static void HistoryEventId(Guid value)
    { if (value == Guid.Empty) throw new ArgumentException("An event identity is required.", nameof(value)); }
    private static bool HistoryEventExists(SqliteConnection c, SqliteTransaction t, Guid id) =>
        (long)Scalar(c, t, "SELECT EXISTS(SELECT 1 FROM Events WHERE id=$event);", ("$event", id.ToString("N")))! != 0;
}
