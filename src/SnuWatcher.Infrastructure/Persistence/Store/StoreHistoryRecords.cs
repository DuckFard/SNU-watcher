using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Core;

namespace SnuWatcher.Infrastructure.Persistence;

public sealed partial class SqliteWatchStore
{
    Task<AlertRecipientPage?> IAlertHistoryReader.ReadRecipientsAsync(Guid eventId, AlertRecipientCursor? after,
        int pageSize, CancellationToken cancellationToken)
    {
        HistoryEventId(eventId); HistoryPageSize(pageSize);
        if (after is not null && after.EventId != eventId) throw new ArgumentException("Recipient cursor identifies another event.", nameof(after));
        return HistoryReadAsync<AlertRecipientPage?>((connection, transaction, stamp) =>
        {
            if (!HistoryEventExists(connection, transaction, eventId)) return null;
            using var query = Command(connection, transaction, StoreHistoryQueries.Recipients
                + (after is null ? "" : StoreHistoryQueries.LaterRecipient) + StoreHistoryQueries.RecipientOrder,
                ("$event", eventId.ToString("N")), ("$take", pageSize + 1));
            if (after is not null)
            {
                query.Parameters.AddWithValue("$subscription", after.AfterSubscriptionId.ToString("N"));
                query.Parameters.AddWithValue("$generation", after.AfterGeneration);
            }
            List<AlertHistoryRecipient> recipients = [];
            using (var rows = query.ExecuteReader())
                while (rows.Read()) recipients.Add(StoreHistoryWire.Recipient(rows));
            var more = recipients.Count > pageSize;
            var items = recipients.Take(pageSize).ToArray();
            var cursor = more ? new AlertRecipientCursor(eventId, items[^1].SubscriptionId, items[^1].Generation) : null;
            return new AlertRecipientPage(eventId, stamp, items, more, cursor);
        }, cancellationToken);
    }

    Task<AlertAttemptPage?> IAlertHistoryReader.ReadAttemptsAsync(Guid eventId, AlertAttemptCursor? after,
        int pageSize, CancellationToken cancellationToken)
    {
        HistoryEventId(eventId); HistoryPageSize(pageSize);
        if (after is not null && after.EventId != eventId) throw new ArgumentException("Attempt cursor identifies another event.", nameof(after));
        return HistoryReadAsync<AlertAttemptPage?>((connection, transaction, stamp) =>
        {
            var context = ReadHistoryAttemptContext(connection, transaction, eventId);
            if (context is null) return null;
            List<AlertHistoryAttempt> items = [];
            var (key, batch) = context.Value;
            if (batch is not null && after?.Origin != AlertAttemptOrigin.EventResult)
                ReadHistoryBatchRecords(connection, transaction, eventId, batch.Value, key,
                    after?.AfterSequence, pageSize + 1, items);
            if (items.Count <= pageSize)
                ReadHistoryEventRecords(connection, transaction, eventId,
                    after?.Origin == AlertAttemptOrigin.EventResult ? after.AfterSequence : null,
                    pageSize + 1 - items.Count, items);
            var more = items.Count > pageSize;
            var page = items.Take(pageSize).ToArray();
            var cursor = more ? new AlertAttemptCursor(eventId, page[^1].Origin, page[^1].Sequence) : null;
            return new AlertAttemptPage(eventId, stamp, page, more, cursor);
        }, cancellationToken);
    }

    private static (SectionKey Key, Guid? BatchId)? ReadHistoryAttemptContext(SqliteConnection c, SqliteTransaction t, Guid eventId)
    {
        using var query = Command(c, t, StoreHistoryQueries.AttemptContext, ("$event", eventId.ToString("N")));
        using var row = query.ExecuteReader();
        if (!row.Read()) return null;
        var key = StoreWire.ReadKey(row.GetString(0));
        if (row.IsDBNull(1)) return (key, null);
        var batch = StoreHistoryWire.Id(row.GetString(1));
        if (row.IsDBNull(2) || StoreWire.ReadKey(row.GetString(2)) != key)
            throw new InvalidDataException("Associated batch section disagrees with event.");
        return (key, batch);
    }

    private static void ReadHistoryBatchRecords(SqliteConnection c, SqliteTransaction t, Guid eventId, Guid batchId, SectionKey key,
        long? sequence, int take, List<AlertHistoryAttempt> items)
    {
        using var query = Command(c, t, StoreHistoryQueries.BatchRecords(sequence is not null), ("$event", eventId.ToString("N")),
            ("$batch", batchId.ToString("N")), ("$take", take));
        if (sequence is { } position) query.Parameters.AddWithValue("$sequence", position);
        using var rows = query.ExecuteReader();
        while (rows.Read())
        {
            var attempt = StoreHistoryWire.Batch(rows);
            if (attempt.Content.Key != key) throw new InvalidDataException("Attempt snapshot section disagrees with event and batch.");
            items.Add(new(eventId, attempt, null, rows.GetBoolean(7)));
        }
    }
    private static void ReadHistoryEventRecords(SqliteConnection c, SqliteTransaction t, Guid eventId,
        long? sequence, int take, List<AlertHistoryAttempt> items)
    {
        using var query = Command(c, t, StoreHistoryQueries.EventRecords(sequence is not null), ("$event", eventId.ToString("N")), ("$take", take));
        if (sequence is { } position) query.Parameters.AddWithValue("$sequence", position);
        using var rows = query.ExecuteReader();
        while (rows.Read()) items.Add(new(eventId, null, StoreHistoryWire.EventResult(rows, 0), true));
    }

    private static AlertHistoryItem ReadHistoryItem(SqliteDataReader row)
    {
        var id = StoreHistoryWire.Id(row.GetString(0));
        var key = StoreWire.ReadKey(row.GetString(1));
        var minimum = row.GetInt32(16); var maximum = row.GetInt32(17);
        if (minimum < 0 || maximum > 1) throw new InvalidDataException("Unknown original opening reason.");
        var trigger = StoreHistoryWire.Trigger(row.GetString(3), id, key, row.GetInt64(2),
            StoreWire.Time(row.GetString(4)), row.GetInt32(15), minimum == 0, maximum == 1);
        var batch = row.IsDBNull(8) ? (Guid?)null : StoreHistoryWire.Id(row.GetString(8));
        if (batch is not null && StoreWire.ReadKey(row.GetString(9)) != key) throw new InvalidDataException("Batch section disagrees with event.");
        var unresolved = row.IsDBNull(10) ? (Guid?)null : StoreHistoryWire.Id(row.GetString(10));
        var result = row.IsDBNull(11) ? null : StoreHistoryWire.EventResult(row, 11);
        return new(trigger, new(row.GetInt64(5), (Application.Persistence.DeliveryStatus)row.GetInt32(6), row.GetInt32(7), batch, unresolved, result));
    }
}
