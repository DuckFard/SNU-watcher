using System.Globalization;
using System.Text;
using System.Text.Json;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Monitoring.SourcePolicy;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Application.Presentation.Terms;
using SnuWatcher.Core;

namespace SnuWatcher.Infrastructure.Persistence;

/// <summary>Explicit versioned, bounded UI storage. Hints never deserialize into source authority.</summary>
internal static class StoreUiWire
{
    private const int ValuesLimit = 2048;
    private const int ReceiptLimit = 4096;
    private const int TermLimit = 1024 * 1024;

    internal static string Values(UiPreferencesValues values) => Serialize(ValuesObject(values), ValuesLimit);
    internal static UiPreferencesValues ReadValues(string json)
    {
        using var document = Parse(json, ValuesLimit);
        return ReadValuesObject(document.RootElement);
    }

    internal static string CommandFacts(SaveUiPreferencesCommand command) => Serialize(new
    { version = 1, id = command.CommandId.ToString("N"), expected = command.ExpectedRevision, values = ValuesObject(command.Values) }, ReceiptLimit);

    internal static SaveUiPreferencesCommand ReadCommandFacts(string json)
    {
        using var document = Parse(json, ReceiptLimit);
        var value = document.RootElement;
        Fields(value, "version", "id", "expected", "values");
        Version(value);
        return new(Guid.ParseExact(Text(value, "id"), "N"), Number(value, "expected"), ReadValuesObject(value.GetProperty("values")));
    }

    internal static string Receipt(UiPreferencesSaveReceipt receipt) => Serialize(new
    {
        version = 1, id = receipt.CommandId.ToString("N"), previous = receipt.PreviousRevision,
        revision = receipt.SavedSnapshot.Revision, saved = StoreWire.At(receipt.SavedSnapshot.SavedAtUtc!.Value),
        values = ValuesObject(receipt.SavedSnapshot.Values),
    }, ReceiptLimit);

    internal static UiPreferencesSaveReceipt ReadReceipt(string json)
    {
        using var document = Parse(json, ReceiptLimit);
        var value = document.RootElement;
        Fields(value, "version", "id", "previous", "revision", "saved", "values");
        Version(value);
        return new(Guid.ParseExact(Text(value, "id"), "N"), Number(value, "previous"),
            new(Number(value, "revision"), ReadValuesObject(value.GetProperty("values")), StoreWire.Time(Text(value, "saved"))));
    }

    private static object ValuesObject(UiPreferencesValues values) => new
    {
        version = 1,
        language = values.Language switch { UiLanguage.Korean => "ko", UiLanguage.English => "en", _ => throw Invalid() },
        year = values.SelectedYear,
        term = values.TermHint is { } term ? new { year = term.Year, selector = term.Selector } : null,
        profile = values.DesiredProfile switch
        { WatchMonitorProfile.Faster => "faster", WatchMonitorProfile.Normal => "normal", WatchMonitorProfile.LowActivity => "low-activity", _ => throw Invalid() },
        close = values.CloseBehavior switch { UiCloseBehavior.MinimizeToTray => "tray", UiCloseBehavior.Quit => "quit", _ => throw Invalid() },
        onboarding = values.OnboardingVersion,
    };

    private static UiPreferencesValues ReadValuesObject(JsonElement value)
    {
        Fields(value, "version", "language", "year", "term", "profile", "close", "onboarding");
        Version(value);
        var term = value.GetProperty("term");
        AcademicTermHint? hint = null;
        if (term.ValueKind != JsonValueKind.Null)
        {
            Fields(term, "year", "selector");
            hint = new(term.GetProperty("year").GetInt32(), Text(term, "selector"));
        }
        return new(Text(value, "language") switch { "ko" => UiLanguage.Korean, "en" => UiLanguage.English, _ => throw Invalid() },
            value.GetProperty("year").ValueKind == JsonValueKind.Null ? null : value.GetProperty("year").GetInt32(), hint,
            Text(value, "profile") switch
            { "faster" => WatchMonitorProfile.Faster, "normal" => WatchMonitorProfile.Normal, "low-activity" => WatchMonitorProfile.LowActivity, _ => throw Invalid() },
            Text(value, "close") switch { "tray" => UiCloseBehavior.MinimizeToTray, "quit" => UiCloseBehavior.Quit, _ => throw Invalid() },
            value.GetProperty("onboarding").GetInt32());
    }

    internal static string Terms(IEnumerable<CachedTermHint> terms) => Serialize(new
    {
        version = 1,
        terms = terms.OrderBy(term => term.Selector, StringComparer.Ordinal)
            .Select(term => new { selector = term.Selector, name = term.Name, english = term.EnglishName }),
    }, TermLimit);

    internal static CachedTermHint[] ReadTerms(string json)
    {
        using var document = Parse(json, TermLimit);
        var value = document.RootElement;
        Fields(value, "version", "terms");
        Version(value);
        var terms = value.GetProperty("terms");
        if (terms.GetArrayLength() is < 1 or > 128) throw Invalid();
        return terms.EnumerateArray().Select(term =>
        {
            Fields(term, "selector", "name", "english");
            return new CachedTermHint(Text(term, "selector"), Text(term, "name"), OptionalText(term, "english"));
        }).ToArray();
    }

    internal static WatchDefinitionSummary Summary(string json, SectionKey firstKey, int count, SectionSnapshot? state)
    {
        using var document = Parse(json, 16 * 1024);
        var v = document.RootElement;
        Fields(v, "id", "revision", "generation", "kind", "label", "status", "initial", "expiry", "created");
        var expiry = v.GetProperty("expiry");
        return new(Guid.ParseExact(Text(v, "id"), "N"), Number(v, "revision"), Number(v, "generation"),
            (SubscriptionKind)v.GetProperty("kind").GetInt32(), Text(v, "label"), (WatchStatus)v.GetProperty("status").GetInt32(),
            v.GetProperty("initial").GetBoolean(), expiry.ValueKind == JsonValueKind.Null ? null : new WatchExpiry(
                DateOnly.ParseExact(Text(expiry, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Text(expiry, "zone"), StoreWire.Time(Text(expiry, "boundary"))),
            StoreWire.Time(Text(v, "created")), new(firstKey), count, state);
    }

    internal static (Guid Id, long Generation, SubscriptionKind Kind) SummaryIdentity(string json)
    {
        using var document = Parse(json, 16 * 1024);
        var value = document.RootElement;
        return (Guid.ParseExact(Text(value, "id"), "N"), Number(value, "generation"), (SubscriptionKind)value.GetProperty("kind").GetInt32());
    }

    internal static CompletedTransportEvidence Transport(string json, string requestId, SourcePolicySnapshot policy)
    {
        using var document = Parse(json, 16 * 1024);
        var v = document.RootElement;
        Fields(v, "version", "id", "source", "ordinal", "outcome", "completed", "started", "received", "retryKind", "retryTicks", "retryDate", "diagnostic");
        Version(v);
        if (Text(v, "id") != requestId || Text(v, "source") != policy.SourceId
            || Number(v, "ordinal") != policy.Progress.LatestCompletedOrdinal
            || v.GetProperty("outcome").GetInt32() != (int)policy.Progress.LatestOutcome!) throw Invalid();
        return new(Number(v, "ordinal"), OptionalText(v, "started") is { } started ? StoreWire.Time(started) : null,
            OptionalText(v, "received") is { } received ? StoreWire.Time(received) : null, StoreWire.Time(Text(v, "completed")));
    }

    private static string Serialize(object value, int maximum)
    {
        var json = JsonSerializer.Serialize(value);
        if (Encoding.UTF8.GetByteCount(json) > maximum) throw Invalid();
        return json;
    }

    private static JsonDocument Parse(string json, int maximum)
    {
        if (json.Length > maximum || Encoding.UTF8.GetByteCount(json) > maximum) throw Invalid();
        return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
    }

    private static void Fields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var remaining = names.ToHashSet(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!remaining.Remove(property.Name)) throw Invalid();
        if (remaining.Count != 0) throw Invalid();
    }
    private static void Version(JsonElement value) { if (Number(value, "version") != 1) throw Invalid(); }
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw Invalid();
    private static string? OptionalText(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Text(value, name);
    private static long Number(JsonElement value, string name) => value.GetProperty(name).GetInt64();
    private static InvalidDataException Invalid() => new("The stored UI payload is invalid or unsupported.");
}
