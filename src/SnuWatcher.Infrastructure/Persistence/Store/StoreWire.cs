using System.Globalization;
using System.Text.Json;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Infrastructure.Persistence;

/// <summary>Versioned explicit storage projections; never relies on DTO constructor binding or collection reference equality.</summary>
internal static class StoreWire
{
    internal static string At(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    internal static DateTimeOffset Time(string value) => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None);
    internal static string Key(SectionKey key) => JsonSerializer.Serialize(new object[]
        { key.Year, key.SemesterCode, key.DetailSemesterCode, key.CourseCode, key.Section, key.SubsubjectCode });

    internal static SectionKey ReadKey(string value)
    {
        using var document = Parse(value);
        var key = document.RootElement;
        if (key.GetArrayLength() != 6) throw new InvalidDataException("Invalid section storage identity.");
        return new SectionKey(key[0].GetInt32(), key[1].GetString()!, key[2].GetString()!, key[3].GetString()!, key[4].GetString()!, key[5].GetString()!);
    }

    internal static string CreateFacts(NewSubscription command) => JsonSerializer.Serialize(new
    {
        version = 1, id = command.Id.ToString("N"), label = command.Label, kind = (int)command.Kind,
        sections = command.Sections.Select(Key).Order(StringComparer.Ordinal), initial = command.InitialNoticeEnabled,
        expiry = Expiry(command.Expiry), created = At(command.CreatedAtUtc),
    });

    internal static string Subscription(SubscriptionSnapshot value) => JsonSerializer.Serialize(new
    {
        id = value.Id.ToString("N"), revision = value.DefinitionRevision, generation = value.ActivationGeneration,
        kind = (int)value.Kind, label = value.Label, status = (int)value.Status, initial = value.InitialNoticeEnabled,
        expiry = Expiry(value.Expiry), created = At(value.CreatedAtUtc),
    });

    internal static SubscriptionSnapshot ReadSubscription(string json, IEnumerable<MemberSnapshot> members)
    {
        using var document = Parse(json);
        var value = document.RootElement;
        return new SubscriptionSnapshot(Guid.ParseExact(Text(value, "id"), "N"), Number(value, "revision"), Number(value, "generation"),
            (SubscriptionKind)Integer(value, "kind"), Text(value, "label"), (WatchStatus)Integer(value, "status"),
            value.GetProperty("initial").GetBoolean(), ReadExpiry(value.GetProperty("expiry")), Time(Text(value, "created")), members);
    }

    internal static (Guid Id, long Generation) SubscriptionIdentity(string json)
    {
        using var document = Parse(json);
        return (Guid.ParseExact(Text(document.RootElement, "id"), "N"), Number(document.RootElement, "generation"));
    }

    internal static string Section(SectionSnapshot value) => JsonSerializer.Serialize(new
    {
        key = Key(value.Key), version = value.Version, ordinal = value.LatestCompletedOrdinal,
        outcome = value.LatestOutcome is { } outcome ? (int?)outcome : null,
        attempted = value.LatestAttemptAtUtc is { } attempted ? At(attempted) : null,
        success = value.LastSuccessfulAtUtc is { } success ? At(success) : null,
        baseline = Baseline(value.Baseline),
    });

    internal static SectionSnapshot ReadSection(string json)
    {
        using var document = Parse(json);
        var value = document.RootElement;
        var key = ReadKey(Text(value, "key"));
        return new SectionSnapshot(key, Number(value, "version"), Number(value, "ordinal"),
            OptionalInteger(value, "outcome") is { } outcome ? (CheckOutcome?)outcome : null,
            OptionalText(value, "attempted") is { } attempted ? Time(attempted) : null,
            OptionalText(value, "success") is { } success ? Time(success) : null, ReadBaseline(value.GetProperty("baseline"), key));
    }

    internal static string SourceFacts(SectionCompletion completion)
    {
        var observation = completion.Observation;
        if (observation.Meaning is { } meaning && (string.IsNullOrWhiteSpace(meaning.Id) || meaning.Id.Length > 80))
            throw new ArgumentException("A quota meaning needs a bounded identity.", nameof(completion));
        return JsonSerializer.Serialize(new
        {
            version = 1, token = completion.Token.Id.ToString("N"), ordinal = completion.Token.Ordinal,
            key = Key(completion.Key), observed = At(completion.ObservedAtUtc), kind = (int)observation.Kind,
            registered = observation.Seats?.Registered, capacity = observation.Seats?.Capacity,
            meaning = observation.Meaning?.Id, outcome = (int)completion.Outcome, diagnostic = completion.DiagnosticCode,
        });
    }

    internal static string Effects(SectionEffects effects) => JsonSerializer.Serialize(new
    {
        outcome = (int)effects.EvaluationOutcome, health = (int)effects.HealthOutcome, baseline = Baseline(effects.NextBaseline),
        initialized = effects.Initializations.OrderBy(i => i.SubscriptionId).ThenBy(i => i.Generation)
            .Select(i => new { id = i.SubscriptionId.ToString("N"), generation = i.Generation }),
        notification = effects.Notification is { } notification ? NotificationValue(notification) : null,
    });

    internal static string Notification(OpeningNotification notification) => JsonSerializer.Serialize(NotificationValue(notification));

    internal static OpeningNotification ReadNotification(string json)
    {
        using var document = Parse(json);
        var value = document.RootElement;
        var recipients = value.GetProperty("recipients").EnumerateArray().Select(r =>
            new Recipient(Text(r, "id"), Number(r, "generation"), (OpeningReason)Integer(r, "reason"))).ToArray();
        return new OpeningNotification(ReadKey(Text(value, "key")), Integer(value, "seats"), OptionalInteger(value, "previous"),
            Number(value, "ordinal"), Time(Text(value, "observed")), recipients);
    }

    internal static string Receipt(SectionCommitReceipt receipt) => JsonSerializer.Serialize(new
    {
        token = receipt.Token.Id.ToString("N"), ordinal = receipt.Token.Ordinal, key = Key(receipt.Key),
        disposition = (int)receipt.Disposition, version = receipt.CommittedSectionVersion, eventId = receipt.EventId?.ToString("N"),
    });

    internal static SectionCommitReceipt ReadReceipt(string json)
    {
        using var document = Parse(json);
        var value = document.RootElement;
        return new SectionCommitReceipt(new CheckAttemptToken(Guid.ParseExact(Text(value, "token"), "N"), Number(value, "ordinal")),
            ReadKey(Text(value, "key")), (CommitDisposition)Integer(value, "disposition"), Number(value, "version"),
            OptionalText(value, "eventId") is { } id ? Guid.ParseExact(id, "N") : null);
    }

    private static object? Baseline(SeatBaseline? value) => value is null ? null : new
    {
        registered = value.Seats.Registered, capacity = value.Seats.Capacity, meaning = value.Meaning.Id,
        ordinal = value.AttemptSequence, observed = At(value.ObservedAt),
    };

    private static SeatBaseline? ReadBaseline(JsonElement value, SectionKey key) => value.ValueKind == JsonValueKind.Null ? null :
        new(key, new SeatCount(Integer(value, "registered"), Integer(value, "capacity")), new QuotaMeaning(Text(value, "meaning")),
            Number(value, "ordinal"), Time(Text(value, "observed")));

    private static object? Expiry(WatchExpiry? value) => value is null ? null : new
    {
        date = value.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), zone = value.ZoneId, boundary = At(value.ExclusiveUtc),
    };

    private static WatchExpiry? ReadExpiry(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : new(
        DateOnly.ParseExact(Text(value, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture), Text(value, "zone"), Time(Text(value, "boundary")));

    private static object NotificationValue(OpeningNotification value) => new
    {
        key = Key(value.Key), seats = value.CurrentSeats, previous = value.PreviousSeats, ordinal = value.AttemptSequence,
        observed = At(value.ObservedAt), recipients = value.Recipients.OrderBy(r => r.SubscriptionId, StringComparer.Ordinal).ThenBy(r => r.Generation)
            .Select(r => new { id = r.SubscriptionId, generation = r.Generation, reason = (int)r.Reason }),
    };

    private static JsonDocument Parse(string json)
    {
        if (json.Length > 4 * 1024 * 1024) throw new InvalidDataException("Stored JSON exceeds its resource bound.");
        return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
    }

    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw new InvalidDataException("Required stored text is absent.");
    private static long Number(JsonElement value, string name) => value.GetProperty(name).GetInt64();
    private static int Integer(JsonElement value, string name) => value.GetProperty(name).GetInt32();
    private static string? OptionalText(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Text(value, name);
    private static int? OptionalInteger(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Integer(value, name);
}
