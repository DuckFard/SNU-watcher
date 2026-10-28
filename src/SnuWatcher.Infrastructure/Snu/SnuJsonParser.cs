using System.Globalization;
using System.Text.Json;

namespace SnuWatcher.Infrastructure.Snu;

internal static class SnuJsonParser
{
    internal static SourceCatalog Catalog(string json, int year)
    {
        using var document = Read(json);
        var root = document.RootElement;
        var terms = Required(root, "SHTM");
        if (terms.ValueKind != JsonValueKind.Array) throw Changed();
        var parsedTerms = terms.EnumerateArray().Select(item => new SourceTerm(year, Text(item, "cmmnCd") ?? "", Text(item, "korNm") ?? "", Text(item, "engNm"))).ToArray();
        if (parsedTerms.Length == 0 || parsedTerms.Any(term => !SourceSectionIdentity.Valid(term.Selector) || term.Name.Length == 0)) throw Changed();
        var departments = new List<SourceDepartment>();
        foreach (var level in new[] { "UP_DEPT", "DEPT", "MJ" })
        {
            if (!root.TryGetProperty(level, out var values)) continue;
            if (values.ValueKind != JsonValueKind.Array) throw Changed();
            foreach (var item in values.EnumerateArray())
            {
                var code = Text(item, "deptCd") ?? Text(item, "cmmnCd");
                var name = Text(item, "deptKorNm") ?? Text(item, "korNm");
                if (!SourceSectionIdentity.Valid(code) || string.IsNullOrWhiteSpace(name)) throw Changed();
                departments.Add(new(code!, name, Text(item, "deptEngNm") ?? Text(item, "engNm"), level));
            }
        }
        return new(year, Array.AsReadOnly(parsedTerms), departments.AsReadOnly());
    }

    internal static SourceDetails Details(string json, SourceSectionIdentity requested)
    {
        using var document = Read(json);
        var root = document.RootElement;
        var metadata = Required(root, "LISTTAB01");
        if (metadata.ValueKind != JsonValueKind.Object) throw Changed();
        SourceSectionIdentity identity;
        try
        {
            identity = new(int.Parse(Text(metadata, "openSchyy") ?? "", CultureInfo.InvariantCulture),
                Text(metadata, "openShtmFg") ?? "", Text(metadata, "openDetaShtmFg") ?? "", Text(metadata, "sbjtCd") ?? "",
                Text(metadata, "ltNo") ?? "", Text(metadata, "sbjtSubhCd") ?? "");
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException) { throw Changed(); }
        if (identity != requested) throw Changed();
        var times = Strings(root, "ltTime");
        var types = Strings(root, "ltType");
        var rooms = Strings(root, "ltRoom");
        if (types.Length != times.Length || rooms.Length != times.Length) throw Changed();
        var meetings = times.Select((time, index) => new SourceMeeting(time, types[index], rooms[index])).ToArray();
        var name = Text(metadata, "sbjtNm");
        if (string.IsNullOrWhiteSpace(name)) throw Changed();
        return new(identity, name, Text(metadata, "sbjtEngNm"), Text(metadata, "profNm"), Text(metadata, "profEngNm"),
            Text(metadata, "departmentKorNm"), Decimal(metadata, "openPnt"), Integer(metadata, "tlsnAplyCapaCnt"),
            Text(metadata, "lsnProgLang"), Text(metadata, "openLtRemk"), Array.AsReadOnly(meetings));
    }

    private static JsonDocument Read(string json)
    {
        try
        {
            var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
            throw Changed();
        }
        catch (JsonException) { throw Changed(); }
    }

    private static JsonElement Required(JsonElement element, string key) => element.TryGetProperty(key, out var value) ? value : throw Changed();
    private static string? Text(JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Changed();
        return element.TryGetProperty(key, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => null
        } : null;
    }
    private static int? Integer(JsonElement element, string key) => int.TryParse(Text(element, key), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value <= 100_000 ? value : null;
    private static decimal? Decimal(JsonElement element, string key) => decimal.TryParse(Text(element, key), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;
    private static string[] Strings(JsonElement element, string key)
    {
        var array = Required(element, key);
        if (array.ValueKind != JsonValueKind.Array || array.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String)) throw Changed();
        return array.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }
    private static SnuSourceException Changed() => new(SourceFailureKind.ParseChanged, "SNU changed the expected public JSON contract.");
}
