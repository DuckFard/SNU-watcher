using System.Text.Json;
using SnuWatcher.Application.Monitoring.SourcePolicy;

namespace SnuWatcher.Infrastructure.Persistence;

/// <summary>Explicit bounded source-policy projections, independent of serializer constructor inference.</summary>
internal static class StoreSourcePolicyWire
{
    internal static string ResultFacts(SourceRequestResultCommand value) => JsonSerializer.Serialize(new
    {
        version = 1, id = value.Token.Id.ToString("N"), source = value.Token.SourceId, ordinal = value.Token.Ordinal,
        outcome = (int)value.Outcome, completed = StoreWire.At(value.CompletedAtUtc), started = At(value.ActualStartedAtUtc),
        received = At(value.ReceivedAtUtc), retryKind = value.RetryAfter is { } retry ? (int?)retry.Kind : null,
        retryTicks = value.RetryAfter?.DeltaDelay?.Ticks, retryDate = At(value.RetryAfter?.HttpDateUtc), diagnostic = value.DiagnosticCode,
    });

    internal static string Policy(SourcePolicySnapshot value) => JsonSerializer.Serialize(new
    {
        version = 1, source = value.SourceId, revision = value.Revision, issued = value.Progress.LastIssuedOrdinal,
        issuedAt = At(value.Progress.LastIssuedAtUtc), completed = value.Progress.LatestCompletedOrdinal,
        outcome = value.Progress.LatestOutcome is { } outcome ? (int?)outcome : null,
        completedAt = At(value.Progress.LatestCompletedAtUtc), success = value.Progress.LastSuccessfulOrdinal,
        successAt = At(value.Progress.LastSuccessfulAtUtc), resume = value.ResumeWatermark,
        pauseOrdinal = value.AccessPause?.CausingOrdinal, pauseAt = At(value.AccessPause?.PausedAtUtc), pauseReason = value.AccessPause?.ReasonCode,
        cooldownAt = At(value.Cooldown?.EstablishedAtUtc), cooldownTicks = value.Cooldown?.Delay.Ticks,
    });

    internal static SourcePolicySnapshot ReadPolicy(string json)
    {
        using var document = Parse(json);
        var v = document.RootElement;
        if (Number(v, "version") != 1) throw new InvalidDataException("Unsupported source-policy storage version.");
        var progress = new SourceRequestProgress(Number(v, "issued"), Time(v, "issuedAt"), Number(v, "completed"),
            OptionalNumber(v, "outcome") is { } outcome ? (SourceRequestOutcome?)outcome : null, Time(v, "completedAt"),
            Number(v, "success"), Time(v, "successAt"));
        var pause = OptionalNumber(v, "pauseOrdinal") is { } ordinal ? new SourceAccessPause(ordinal,
            Time(v, "pauseAt") ?? throw new InvalidDataException("Missing pause timestamp."), Text(v, "pauseReason")) : null;
        var cooldown = Time(v, "cooldownAt") is { } at ? new SourceCooldown(at,
            TimeSpan.FromTicks(Number(v, "cooldownTicks"))) : null;
        return new(Text(v, "source"), Number(v, "revision"), progress, pause, cooldown, Number(v, "resume"));
    }

    internal static string Receipt(SourcePolicyReceipt value) => JsonSerializer.Serialize(new
    { version = 1, id = value.Token.Id.ToString("N"), source = value.Token.SourceId, ordinal = value.Token.Ordinal, applied = value.PolicyApplied, revision = value.PolicyRevision });

    internal static SourcePolicyReceipt ReadReceipt(string json)
    {
        using var document = Parse(json);
        var v = document.RootElement;
        if (Number(v, "version") != 1) throw new InvalidDataException("Unsupported source-policy receipt version.");
        return new(new(Guid.ParseExact(Text(v, "id"), "N"), Text(v, "source"), Number(v, "ordinal")),
            v.GetProperty("applied").GetBoolean(), Number(v, "revision"));
    }

    private static string? At(DateTimeOffset? value) => value is { } at ? StoreWire.At(at) : null;
    private static string Text(JsonElement v, string name) => v.GetProperty(name).GetString() ?? throw new InvalidDataException("Missing stored policy text.");
    private static long Number(JsonElement v, string name) => v.GetProperty(name).GetInt64();
    private static long? OptionalNumber(JsonElement v, string name) => v.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Number(v, name);
    private static DateTimeOffset? Time(JsonElement v, string name) => v.GetProperty(name).ValueKind == JsonValueKind.Null ? null : StoreWire.Time(Text(v, name));
    private static JsonDocument Parse(string json)
    {
        if (json.Length > 16 * 1024) throw new InvalidDataException("Stored source policy exceeds its resource bound.");
        return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
    }
}
