using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Snu;
using SnuWatcher.Infrastructure.Snu.Gateway;
using Xunit;

namespace SnuWatcher.Integration.Tests;

/// <summary>Single-page browsing, the site's stated totals, grade and program filters, card tags and the current term.</summary>
public sealed class SnuBrowseTests
{
    private static readonly SourceTerm Winter = new(2026, "U000200002U000300002", "겨울학기", "Winter Semester");
    private static readonly DateTimeOffset DoneAt = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AnUnfilteredPageReadsExactlyThatPageAndReportsTheSitesTotalAndLastPage()
    {
        var handler = new Capture(Page(3, total: 279, links: [1, 2, 4, 5, 6, 28]));
        using var source = Client(handler);
        var result = await source.SearchAsync(new(Winter, Page: 3));
        Assert.True(result.IsComplete);
        Assert.Empty(result.Failures);
        Assert.Equal((1, 279, 28), (result.PagesFetched, result.TotalCount, result.LastPage));
        var section = Assert.Single(result.Sections);
        Assert.NotNull(section.ObservedAtUtc);
        Assert.Equal(("[학사] [전선]", 3m), (section.Classification, section.Credits));
        var body = Assert.Single(handler.Bodies);
        Assert.Contains("pageNo=3", body);
        Assert.Contains("srchSbjtNm=&", body);
        Assert.DoesNotContain("srchOpenShyr", body);
        Assert.DoesNotContain("srchCptnCorsFg", body);
    }

    [Fact]
    public async Task ASinglePageWithoutLinksIsItsOwnLastPageAndUnstatedTotalsStayUnknown()
    {
        using var source = Client(new Capture(Page(1, total: null, links: [])));
        var result = await source.SearchAsync(new(Winter, Page: 1));
        Assert.Equal((null, 1), (result.TotalCount, result.LastPage));
        Assert.Null(Assert.Single(result.Sections).Classification);
    }

    [Fact]
    public async Task GradeAndProgramAreSentAsTheSitesOwnFieldsOnlyWhenChosen()
    {
        var handler = new Capture(Page(1, total: 12, links: [2]));
        using var source = Client(handler);
        await source.SearchAsync(new(Winter, Page: 1, Grade: "2", Level: "C013300001"));
        var body = Assert.Single(handler.Bodies);
        Assert.Contains("srchOpenShyr=2", body);
        Assert.Contains("srchCptnCorsFg=C013300001", body);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2001)]
    public async Task PagesOutsideTheBrowseBoundAreRejectedBeforeAnyRequest(int page)
    {
        var handler = new Capture(Page(1, total: 1, links: []));
        using var source = Client(handler);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => source.SearchAsync(new(Winter, Page: page)));
        await Assert.ThrowsAsync<ArgumentException>(() => source.SearchAsync(new(Winter, Grade: "bad grade")));
        Assert.Empty(handler.Bodies);
    }

    [Theory]
    [InlineData("학점 <em>1.5</em>", 1.5)]
    [InlineData("Credits <em>2</em>", 2.0)]
    [InlineData("학점 <em>세</em>", null)]
    public async Task CardCreditsAreReadOnlyFromAStatedNumber(string credits, double? expected)
    {
        using var source = Client(new Capture(Page(1, total: 1, links: []).Replace("학점 <em>3</em>", credits, StringComparison.Ordinal)));
        var section = Assert.Single((await source.SearchAsync(new(Winter, Page: 1))).Sections);
        Assert.Equal(expected is null ? null : (decimal)expected.Value, section.Credits);
    }

    [Fact]
    public async Task TheCatalogReportsTheSitesCurrentTermWithItsOwnYear()
    {
        using var source = Client(new Capture(Metadata(open: 2026, current: 2026, detail: "U000300002")) { Json = true });
        var catalog = await source.GetCatalogAsync(2026);
        Assert.Equal((2026, Winter.Selector), (catalog.CurrentYear, catalog.CurrentSelector));
        using var missing = Client(new Capture(Metadata(open: 2026, current: 2026, detail: null)) { Json = true });
        var unreported = await missing.GetCatalogAsync(2026);
        Assert.Equal((null, null), (unreported.CurrentYear, unreported.CurrentSelector));
    }

    [Fact]
    public void MappingCarriesTheCurrentTermAndDropsAMalformedOneWithoutFailingTheCatalog()
    {
        var query = new CatalogQuery(2026);
        var raw = new SourceCatalog(2026, [Winter], []) { CurrentYear = 2026, CurrentSelector = Winter.Selector };
        var data = SnuDataMapper.Catalog(query, raw, DoneAt).Data!;
        Assert.Equal(Winter.Selector, data.CurrentTerm!.Selector);
        var otherYear = SnuDataMapper.Catalog(query, raw with { CurrentYear = 2025 }, DoneAt).Data!;
        Assert.Null(otherYear.CurrentTerm);
        Assert.Equal(2025, otherYear.CurrentYear);
        var malformed = SnuDataMapper.Catalog(query, raw with { CurrentSelector = "not a selector" }, DoneAt);
        Assert.Equal(SourceReadStatus.Complete, malformed.Status);
        Assert.Null(malformed.Data!.CurrentTermSelector);
        Assert.Null(SnuDataMapper.Catalog(query, raw with { CurrentYear = null }, DoneAt).Data!.CurrentYear);
    }

    [Fact]
    public void SearchMappingCarriesPagingFactsAndDropsUnusableTotalsTagsAndCredits()
    {
        var query = new CourseSearchQuery(2026, Winter.Selector, page: 2, grade: "1", level: "C013300001");
        var catalog = SnuDataMapper.Catalog(new(2026), new(2026, [Winter], []), DoneAt).Data!;
        Assert.True(SnuIdentityMapper.TryCreateSearchQuery(query, catalog, out var dispatched));
        Assert.Equal((2, "1", "C013300001"), (dispatched!.Page, dispatched.Grade, dispatched.Level));
        var identity = new SourceSectionIdentity(2026, "U000200002", "U000300002", "F32.102", "012", "000");
        var section = new SourceSection(identity, "통계학", "교수", "통계학과", "월(09:30~10:45)", 39, 40, 40, "수강신청인원/정원(재학생)")
            { ObservedAtUtc = DoneAt, Classification = "[학사] [전선]", Credits = 3 };
        var result = new SourceSearchResult([section], true, [], 1) { CompletedAtUtc = DoneAt, TotalCount = 279, LastPage = 28 };
        var read = SnuDataMapper.Search(query, dispatched, result, DoneAt);
        Assert.Equal((279, 28), (read.Data!.TotalCount, read.Data.LastPage));
        var row = Assert.Single(read.Data.Rows);
        Assert.Equal(("[학사] [전선]", 3m), (row.Classification, row.Credits));

        var unusable = SnuDataMapper.Search(query, dispatched, result with
        {
            Sections = [section with { Classification = "[학사]\u0001", Credits = 1000 }], TotalCount = -1, LastPage = 5000,
        }, DoneAt);
        Assert.Equal(SourceReadStatus.Complete, unusable.Status);
        Assert.Equal((null, null), (unusable.Data!.TotalCount, unusable.Data.LastPage));
        Assert.Equal((null, null), (Assert.Single(unusable.Data.Rows).Classification, unusable.Data.Rows[0].Credits));

        // A dispatched request for another page or filter can never be mapped onto this query.
        foreach (var other in new[] { dispatched with { Page = 3 }, dispatched with { Grade = "2" }, dispatched with { Level = null } })
            Assert.Equal(SourceReadStatus.Failed, SnuDataMapper.Search(query, other, result, DoneAt).Status);
    }

    private static string Page(int page, int? total, int[] links)
    {
        var anchors = string.Concat(links.Select(number => $"<a href=\"javascript:fnGotoPage({number});\">{number}</a>"));
        var count = total is { } value ? $"<p>{value}건의 교과목이 검색되었습니다.</p>" : "";
        var tags = total is null ? "" : "[학사] [전선] ";
        return "<!doctype html><html lang=\"ko\"><head><title>검색결과 | 강좌검색</title></head><body>" + count +
            "<div class=\"course-info-list\"><div class=\"course-info-item\">" +
            "<input name=\"openSchyy\" value=\"2026\"><input name=\"openShtmFg\" value=\"U000200002\"><input name=\"openDetaShtmFg\" value=\"U000300002\">" +
            $"<input name=\"sbjtCd\" value=\"F32.1{page:00}\"><input name=\"ltNo\" value=\"001\"><input name=\"sbjtSubhCd\" value=\"000\"><input name=\"sbjtNm\" value=\"통계학\">" +
            $"<div class=\"course-name\"> {tags}<strong> 통계학 </strong> </div>" +
            "<ul class=\"course-info\"><li class=\"txt\"><span>예시 교수</span><span>통계학과</span><span>F32.101(001)</span></li>" +
            "<li class=\"txt\"><span>수강신청인원/정원(재학생) <em>35/40 (40)</em></span><span>총수강인원 <em>36</em></span><span>학점 <em>3</em></span>" +
            "<span>월(09:30~10:45)</span></li></ul></div></div><div class=\"cc-paging\">" + anchors + "</div></body></html>";
    }

    private static string Metadata(int open, int current, string? detail)
    {
        var value = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog.json")))!.AsObject();
        value["openSchyy"] = open; value["currSchyy"] = current; value["currShtmFg"] = "U000200002";
        if (detail is null) value.Remove("currDetaShtmFg"); else value["currDetaShtmFg"] = detail;
        return value.ToJsonString();
    }

    private static SnuCourseSource Client(Capture handler) => SnuCourseSource.CreateForTesting(new HttpClient(handler), new(TimeSpan.Zero));

    private sealed class Capture(string response) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public bool Json { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(response, Encoding.UTF8, Json ? "application/json" : "text/html"),
            };
        }
    }
}
