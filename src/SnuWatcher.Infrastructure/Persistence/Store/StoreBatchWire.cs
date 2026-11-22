using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Core;

namespace SnuWatcher.Infrastructure.Persistence;

internal static class StoreBatchWire
{
    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static object Recipients(IEnumerable<BatchRecipient> values) => values
        .OrderBy(r => r.SubscriptionId.ToString("N"), StringComparer.Ordinal).ThenBy(r => r.Generation)
        .Select(r => new { id = r.SubscriptionId.ToString("N"), generation = r.Generation, reasons = r.Reasons.Select(x => (int)x) });
    internal static object Coverage(BatchCoverageSummary v) => new { count = v.EventCount, digest = v.Digest, min = v.MinTriggerOrdinal, max = v.MaxTriggerOrdinal };

    internal static string ContextFacts(BatchDeliveryContext c) => JsonSerializer.Serialize(new
    {
        version = 1, id = c.Batch.Id.ToString("N"), section = StoreWire.Key(c.Batch.Key), batchRevision = c.Batch.Revision,
        status = (int)c.Batch.Status, cutoff = c.Batch.FoldCutoffOrdinal, cursor = c.Batch.FoldCursorOrdinal, complete = c.Batch.FoldComplete,
        sectionFacts = StoreWire.Section(c.Section), catalog = c.CatalogRevision, ageTicks = c.Policy.MaxAge.Ticks,
        maxAttempts = c.Policy.MaxAttempts, validUntil = c.ValidUntilUtc is { } until ? StoreWire.At(until) : null,
        pending = c.PendingCount, ineligible = c.IneligibleCount, exhausted = c.BudgetExhaustedCount,
        coverage = Coverage(c.EligibleCoverage), facts = c.AllFactsDigest, recipients = Recipients(c.EligibleRecipients),
        unresolved = c.UnresolvedAttemptId?.ToString("N"), overflow = c.RecipientLimitExceeded,
    });

    internal static string DecisionFacts(BatchDeliveryDecision d) => JsonSerializer.Serialize(new
    {
        disposition = (int)d.Disposition, reason = d.ReasonCode,
        content = d.Content is { } content ? new
        {
            key = StoreWire.Key(content.Key), registered = content.CurrentCounts.Registered, capacity = content.CurrentCounts.Capacity,
            success = StoreWire.At(content.CurrentSuccessAtUtc), ordinal = content.CurrentSourceOrdinal,
            recipients = Recipients(content.EligibleRecipients), coverage = Coverage(content.Coverage),
        } : null,
    });

    internal static string PreparedFacts(PreparedBatchAttempt p) => Hash(JsonSerializer.Serialize(new
    {
        version = 1, request = p.ReservationRequestId.ToString("N"), context = ContextFacts(p.Context),
        aggregated = StoreWire.At(p.Context.EvaluatedAtUtc), evaluated = StoreWire.At(p.EvaluatedAtUtc), decision = DecisionFacts(p.Decision),
    }));

    internal static string Attempt(BatchAttemptSnapshot v) => JsonSerializer.Serialize(new
    {
        version = 1, id = v.AttemptId.ToString("N"), request = v.ReservationRequestId.ToString("N"), sequence = v.AttemptSequence,
        prepared = StoreWire.At(v.PreparedAtUtc), ageTicks = v.Policy.MaxAge.Ticks, maxAttempts = v.Policy.MaxAttempts,
        batch = v.Envelope.BatchId.ToString("N"), key = StoreWire.Key(v.Envelope.Key),
        registered = v.Envelope.CurrentCounts.Registered, capacity = v.Envelope.CurrentCounts.Capacity,
        success = StoreWire.At(v.Envelope.CurrentSuccessAtUtc), ordinal = v.Envelope.CurrentSourceOrdinal,
        recipients = Recipients(v.Envelope.EligibleRecipients), coverage = Coverage(v.Envelope.Coverage),
    });

    internal static BatchAttemptSnapshot ReadAttempt(string json)
    {
        using var document = Parse(json);
        var v = document.RootElement;
        Version(v);
        var id = Id(v, "id");
        var envelope = new BatchDeliveryEnvelope(Id(v, "batch"), id, StoreWire.ReadKey(Text(v, "key")),
            new SeatCount(v.GetProperty("registered").GetInt32(), v.GetProperty("capacity").GetInt32()),
            StoreWire.Time(Text(v, "success")), Number(v, "ordinal"),
            v.GetProperty("recipients").EnumerateArray().Select(r => new BatchRecipient(Id(r, "id"), Number(r, "generation"),
                r.GetProperty("reasons").EnumerateArray().Select(reason => (OpeningReason)reason.GetInt32()))),
            ReadCoverage(v.GetProperty("coverage")));
        return new(id, Id(v, "request"), Number(v, "sequence"), StoreWire.Time(Text(v, "prepared")),
            new(TimeSpan.FromTicks(Number(v, "ageTicks")), v.GetProperty("maxAttempts").GetInt32()), envelope);
    }

    internal static string ResultFacts(BatchAttemptResultCommand v) => JsonSerializer.Serialize(new
    { version = 1, id = v.AttemptId.ToString("N"), outcome = (int)v.Outcome, diagnostic = v.DiagnosticCode, at = StoreWire.At(v.RecordedAtUtc) });

    internal static string Receipt(BatchAttemptReceipt v) => JsonSerializer.Serialize(new
    {
        version = 1, id = v.AttemptId.ToString("N"), batch = v.BatchId.ToString("N"), outcome = (int)v.Outcome,
        count = v.CoveredEventCount, accepted = v.AcceptedCount, pending = v.PendingCount, failed = v.FailedCount, at = StoreWire.At(v.RecordedAtUtc),
    });

    internal static BatchAttemptReceipt ReadReceipt(string json)
    {
        using var document = Parse(json);
        var v = document.RootElement;
        Version(v);
        return new(Id(v, "id"), Id(v, "batch"), (BatchAttemptResultOutcome)v.GetProperty("outcome").GetInt32(),
            Number(v, "count"), Number(v, "accepted"), Number(v, "pending"), Number(v, "failed"), StoreWire.Time(Text(v, "at")));
    }

    private static BatchCoverageSummary ReadCoverage(JsonElement v) => new(Number(v, "count"), Text(v, "digest"), Number(v, "min"), Number(v, "max"));
    private static string Text(JsonElement v, string name) => v.GetProperty(name).GetString() ?? throw new InvalidDataException("Missing batch storage text.");
    private static long Number(JsonElement v, string name) => v.GetProperty(name).GetInt64();
    private static Guid Id(JsonElement v, string name) => Guid.ParseExact(Text(v, name), "N");
    private static void Version(JsonElement v) { if (Number(v, "version") != 1) throw new InvalidDataException("Unsupported batch wire version."); }
    private static JsonDocument Parse(string json)
    {
        if (json.Length > 2 * 1024 * 1024) throw new InvalidDataException("Stored batch payload exceeds its resource bound.");
        return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
    }
}
