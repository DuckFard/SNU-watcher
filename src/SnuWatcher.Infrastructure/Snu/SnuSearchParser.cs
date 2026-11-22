using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace SnuWatcher.Infrastructure.Snu;

internal static class SnuSearchParser
{
    internal sealed record Page(IReadOnlyList<SourceSection> Sections, IReadOnlyList<int> Pages)
    {
        /// <summary>The stated result count ("N건의 교과목이 검색되었습니다"), when present.</summary>
        public int? TotalCount { get; init; }
    }
    private static readonly Regex PageLink = new(@"fnGotoPage\(\s*['" + "\"" + @"]?(\d+)['" + "\"" + @"]?\s*\)", RegexOptions.CultureInvariant);
    private static readonly Regex Ratio = new(@"^\s*(\d+)\s*/\s*(\d+)(?:\s*\(\s*\d+\s*\))?\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex Total = new(@"(?<!\d)(\d{1,7})\s*건의\s*교과목이\s*검색되었습니다", RegexOptions.CultureInvariant);
    private static readonly Regex Tags = new(@"\A(?:\[[^\[\]]{1,40}\]\s*){1,6}", RegexOptions.CultureInvariant);
    private static readonly Regex CreditText = new(@"\A(?:학점|Credits?)\s*([0-9]{1,3}(?:\.[0-9]{1,2})?)\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static Page Parse(string html, SourceTerm term)
    {
        using var document = new HtmlParser().ParseDocument(html);
        var title = document.Title ?? "";
        if (!title.Contains("검색결과", StringComparison.Ordinal) && !title.Contains("Search Results", StringComparison.OrdinalIgnoreCase))
            throw Changed("SNU served a gate or maintenance page.");
        var cards = document.QuerySelectorAll(".course-info-item");
        var bodyText = Clean(document.Body?.TextContent ?? "");
        var observedEmpty = document.QuerySelector(".course-info-list") is not null &&
            Regex.IsMatch(bodyText, @"(?<!\d)0\s*건의\s*교과목이\s*검색되었습니다\.", RegexOptions.CultureInvariant);
        var explicitEmptyText = bodyText.Equals("No courses found", StringComparison.OrdinalIgnoreCase);
        if (cards.Length == 0 && !observedEmpty && !explicitEmptyText)
            throw Changed("SNU result rows could not be identified.");
        var sections = cards.Select(card => ParseCard(card, term)).ToArray();
        var pages = PageLink.Matches(html).Select(match => int.TryParse(match.Groups[1].Value, out var number) ? number : int.MaxValue).Distinct().ToArray();
        var total = Total.Match(bodyText);
        return new(Array.AsReadOnly(sections), Array.AsReadOnly(pages))
            { TotalCount = total.Success ? int.Parse(total.Groups[1].Value, CultureInfo.InvariantCulture) : null };
    }

    private static SourceSection ParseCard(IElement card, SourceTerm term)
    {
        SourceSectionIdentity identity;
        try
        {
            identity = new(int.Parse(Value(card, "openSchyy"), CultureInfo.InvariantCulture), Value(card, "openShtmFg"),
                Value(card, "openDetaShtmFg"), Value(card, "sbjtCd"), Value(card, "ltNo"), Value(card, "sbjtSubhCd"));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
        { throw Changed("SNU changed the course identity fields."); }
        if (identity.Year != term.Year || term.Selector != identity.SemesterCode + identity.DetailSemesterCode)
            throw Changed("SNU returned a section from a different academic term.");
        var name = Value(card, "sbjtNm");
        if (string.IsNullOrWhiteSpace(name)) throw Changed("SNU returned a section without a course name.");
        var first = card.QuerySelector("li.txt");
        var metadata = first?.Children.Select(child => Clean(child.TextContent)).ToArray() ?? [];
        var countElement = card.QuerySelectorAll("li.txt span")
            .FirstOrDefault(span => span.TextContent.Contains("수강신청인원/정원(재학생)", StringComparison.Ordinal) || span.TextContent.Contains("Enrollment/Quota", StringComparison.OrdinalIgnoreCase));
        var countText = countElement is null ? null : Clean(countElement.TextContent);
        var countToken = countElement?.QuerySelector("em")?.TextContent ?? "";
        var ratio = Ratio.Match(countToken);
        var registered = ratio.Success ? Number(ratio.Groups[1].Value) : null;
        var capacity = ratio.Success ? Number(ratio.Groups[2].Value) : null;
        var total = Regex.Match(Clean(card.TextContent), @"(?:총수강인원|Total Enrollment)\s*(\d+)", RegexOptions.IgnoreCase);
        var schedule = card.QuerySelectorAll("li.txt span").Select(span => Clean(span.TextContent))
            .LastOrDefault(text => Regex.IsMatch(text, @"\d{1,2}:\d{2}"));
        // Optional display facts: tags before the name ("[학사] [전선]") and the "학점 3" credits span.
        var tags = Tags.Match(Clean(card.QuerySelector(".course-name")?.TextContent ?? ""));
        var credits = card.QuerySelectorAll("li.txt span").Select(span => CreditText.Match(Clean(span.TextContent)))
            .FirstOrDefault(match => match.Success);
        return new(identity, Clean(name), metadata.ElementAtOrDefault(0), metadata.ElementAtOrDefault(1), schedule,
            registered, capacity, total.Success ? Number(total.Groups[1].Value) : null,
            countText?.Contains("Enrollment", StringComparison.OrdinalIgnoreCase) == true ? "Enrollment/Quota" : "수강신청인원/정원(재학생)")
        {
            Classification = tags.Success ? tags.Value.Trim() : null,
            Credits = credits is null ? null : decimal.Parse(credits.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture),
        };
    }

    private static int? Number(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number <= 100_000 ? number : null;
    private static string Value(IElement element, string name) => element.QuerySelector($"input[name='{name}']")?.GetAttribute("value")?.Trim() ?? "";
    private static string Clean(string text) => Regex.Replace(text, @"\s+", " ").Trim();
    private static SnuSourceException Changed(string message) => new(SourceFailureKind.ParseChanged, message);
}
