using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Alerts;
using SnuWatcher.Core;

namespace SnuWatcher.Infrastructure.Persistence;

/// <summary>Bounded scalar projections. Recipient arrays are validated as JSON and skipped without allocating recipient DTO arrays.</summary>
internal static class StoreHistoryWire
{
    private static readonly string[] TriggerFields = ["key", "seats", "previous", "ordinal", "observed", "recipients"];
    private static readonly string[] AttemptFields = ["version", "id", "request", "sequence", "prepared", "ageTicks", "maxAttempts",
        "batch", "key", "registered", "capacity", "success", "ordinal", "recipients", "coverage"];
    internal static Guid Id(string value) => Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty && id.ToString("N") == value
        ? id : throw Invalid();

    internal static AlertTriggerFacts Trigger(string json, Guid id, SectionKey key, long ordinal, DateTimeOffset created,
        int recipients, bool initial, bool increase)
    {
        var value = Scalars(json, 4 * 1024 * 1024, TriggerFields);
        if (StoreWire.ReadKey(Text(value, "key")) != key || Number(value, "ordinal") != ordinal) throw Invalid();
        return new(id, key, ordinal, Integer(value, "seats"), value["previous"] is null ? null : Integer(value, "previous"),
            StoreWire.Time(Text(value, "observed")), created, recipients, initial, increase);
    }

    internal static AlertEventResult EventResult(SqliteDataReader row, int offset) =>
        new(row.GetInt64(offset), (DeliveryStatus)row.GetInt32(offset + 1), row.GetInt32(offset + 2), StoreWire.Time(row.GetString(offset + 3)));

    internal static AlertHistoryRecipient Recipient(SqliteDataReader row)
    {
        var id = Id(row.GetString(0));
        string? label = null;
        if (!row.IsDBNull(3))
        {
            var json = row.GetString(3);
            if (json.Length > 16 * 1024 || Encoding.UTF8.GetByteCount(json) > 16 * 1024) throw Invalid();
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var value = document.RootElement;
            if (Id(value.GetProperty("id").GetString()!) != id) throw Invalid();
            label = value.GetProperty("label").GetString() ?? throw Invalid();
        }
        return new(id, row.GetInt64(1), (OpeningReason)row.GetInt32(2), label);
    }

    internal static AlertBatchAttempt Batch(SqliteDataReader row)
    {
        var attemptId = Id(row.GetString(0)); var batchId = Id(row.GetString(1)); var sequence = row.GetInt64(2);
        var status = row.GetInt32(3);
        var value = Scalars(row.GetString(4), 2 * 1024 * 1024, AttemptFields);
        if (Number(value, "version") != 1 || Id(Text(value, "id")) != attemptId || Id(Text(value, "batch")) != batchId
            || Number(value, "sequence") != sequence) throw Invalid();
        _ = Id(Text(value, "request"));
        _ = new BatchDeliveryPolicy(TimeSpan.FromTicks(Number(value, "ageTicks")), Integer(value, "maxAttempts"));
        var coverage = (BatchCoverageSummary)value["coverage"]!;
        if (Number(value, "ordinal") < coverage.MaxTriggerOrdinal) throw Invalid();
        var content = new AlertBatchContent(StoreWire.ReadKey(Text(value, "key")),
            new(Integer(value, "registered"), Integer(value, "capacity")), StoreWire.Time(Text(value, "success")),
            Number(value, "ordinal"), coverage.EventCount);
        BatchAttemptResultOutcome? outcome = null;
        DateTimeOffset? recorded = null;
        if (row.IsDBNull(5) != row.IsDBNull(6) || status is not (0 or 1) || (status == 0) != row.IsDBNull(5)) throw Invalid();
        if (status == 1)
        {
            var result = ReadResult(row.GetString(5), row.GetString(6), attemptId, batchId, coverage.EventCount);
            outcome = result.Outcome; recorded = result.RecordedAtUtc;
        }
        return new(attemptId, batchId, sequence, StoreWire.Time(Text(value, "prepared")), content, outcome, recorded);
    }

    private static BatchAttemptResultCommand ReadResult(string facts, string receiptJson, Guid attemptId, Guid batchId, long coverageCount)
    {
        var value = Scalars(facts, 2 * 1024 * 1024, ["version", "id", "outcome", "diagnostic", "at"]);
        if (Number(value, "version") != 1) throw Invalid();
        var command = new BatchAttemptResultCommand(Id(Text(value, "id")), (BatchAttemptResultOutcome)Integer(value, "outcome"),
            Text(value, "diagnostic"), StoreWire.Time(Text(value, "at")));
        _ = Scalars(receiptJson, 2 * 1024 * 1024, ["version", "id", "batch", "outcome", "count", "accepted", "pending", "failed", "at"]);
        var receipt = StoreBatchWire.ReadReceipt(receiptJson);
        if (command.AttemptId != attemptId || receipt.AttemptId != attemptId || receipt.BatchId != batchId
            || receipt.CoveredEventCount != coverageCount || receipt.Outcome != command.Outcome || receipt.RecordedAtUtc != command.RecordedAtUtc) throw Invalid();
        return command;
    }

    private static Dictionary<string, object?> Scalars(string json, int maximumBytes, string[] fields)
    {
        if (json.Length > maximumBytes || Encoding.UTF8.GetByteCount(json) > maximumBytes) throw Invalid();
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { MaxDepth = 16 });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw Invalid();
        var remaining = fields.ToHashSet(StringComparer.Ordinal);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw Invalid();
            var name = reader.GetString()!;
            if (!remaining.Remove(name) || !reader.Read()) throw Invalid();
            if (name == "recipients")
            {
                if (reader.TokenType != JsonTokenType.StartArray) throw Invalid();
                reader.Skip(); values.Add(name, null);
            }
            else if (name == "coverage") values.Add(name, Coverage(ref reader));
            else values.Add(name, reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(), JsonTokenType.Number => reader.GetInt64(),
                JsonTokenType.Null => null, _ => throw Invalid(),
            });
        }
        if (remaining.Count != 0 || reader.TokenType != JsonTokenType.EndObject || reader.Read()) throw Invalid();
        return values;
    }
    private static BatchCoverageSummary Coverage(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw Invalid();
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        var remaining = new HashSet<string>(["count", "digest", "min", "max"], StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject()) if (!remaining.Remove(field.Name)) throw Invalid();
        if (remaining.Count != 0) throw Invalid();
        return new(value.GetProperty("count").GetInt64(), value.GetProperty("digest").GetString()!,
            value.GetProperty("min").GetInt64(), value.GetProperty("max").GetInt64());
    }
    private static long Number(Dictionary<string, object?> value, string key) => value[key] is long number ? number : throw Invalid();
    private static int Integer(Dictionary<string, object?> value, string key) => checked((int)Number(value, key));
    private static string Text(Dictionary<string, object?> value, string key) => value[key] as string ?? throw Invalid();
    private static InvalidDataException Invalid() => new("Stored history data is invalid.");
}
