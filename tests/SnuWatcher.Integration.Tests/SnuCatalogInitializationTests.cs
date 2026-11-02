using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SnuWatcher.Infrastructure.Snu;
using Xunit;

namespace SnuWatcher.Integration.Tests;

public sealed class SnuCatalogInitializationTests
{
    private const string Winter = "U000200002U000300002";
    private const string Regular = "U000200002U000300001";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task InitialMetadataOmitsAnIncompleteYearTermSelectionAndUsesItsReportedYear(string? selector)
    {
        var handler = new Capture(Metadata(2026)); using var source = Client(handler);
        var catalog = await source.GetCatalogAsync(2026, selector);
        Assert.Equal(2026, catalog.Year); Assert.All(catalog.Terms, term => Assert.Equal(2026, term.Year));
        var request = Assert.Single(handler.Requests);
        Assert.Equal("openUpDeptCd=&openDeptCd=", request.Body);
    }

    [Fact]
    public async Task AnotherYearUsesOnlyAReportedSelectorForMetadataAndRequiresAnExplicitYearEcho()
    {
        var handler = new Capture(Metadata(2026), Metadata(2025)); using var source = Client(handler);
        var catalog = await source.GetCatalogAsync(2025, upDepartment: "300", department: "326");
        Assert.Equal(2025, catalog.Year); Assert.All(catalog.Terms, term => Assert.Equal(2025, term.Year));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("openUpDeptCd=300&openDeptCd=326", handler.Requests[0].Body);
        Assert.Contains("srchOpenSchyy=2025", handler.Requests[1].Body);
        Assert.Contains("srchOpenShtm=" + Winter, handler.Requests[1].Body);
        Assert.Contains("openUpDeptCd=300&openDeptCd=326", handler.Requests[1].Body);
    }

    [Theory]
    [InlineData("openSchyy")]
    [InlineData("currSchyy")]
    public async Task MissingSourceYearProofCannotBeReplacedByTheCallersYear(string field)
    {
        var metadata = JsonNode.Parse(Metadata(2026))!.AsObject(); metadata.Remove(field);
        var handler = new Capture(metadata.ToJsonString()); using var source = Client(handler);
        Assert.Equal(SourceFailureKind.ParseChanged,
            (await Assert.ThrowsAsync<SnuSourceException>(() => source.GetCatalogAsync(2026))).Kind);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("openSchyy")]
    [InlineData("currSchyy")]
    public async Task ConflictingDuplicateYearFieldsCannotBecomeLastWinsSourceProof(string field)
    {
        var metadata = Metadata(2026).Replace("\"" + field + "\":2026", "\"" + field + "\":2025,\"" + field + "\":2026", StringComparison.Ordinal);
        using var source = Client(new Capture(metadata));
        Assert.Equal(SourceFailureKind.ParseChanged,
            (await Assert.ThrowsAsync<SnuSourceException>(() => source.GetCatalogAsync(2026))).Kind);
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("2012")]
    [InlineData("2201")]
    public async Task MalformedSourceYearIsRejected(string year)
    {
        var metadata = JsonNode.Parse(Metadata(2026))!.AsObject(); metadata["openSchyy"] = year;
        using var source = Client(new Capture(metadata.ToJsonString()));
        Assert.Equal(SourceFailureKind.ParseChanged,
            (await Assert.ThrowsAsync<SnuSourceException>(() => source.GetCatalogAsync(2026))).Kind);
    }

    [Fact]
    public async Task AFailedYearFollowupNeverReturnsTheDefaultYearCatalog()
    {
        var handler = new Capture(Metadata(2026), Metadata(2026)); using var source = Client(handler);
        Assert.Equal(SourceFailureKind.ParseChanged,
            (await Assert.ThrowsAsync<SnuSourceException>(() => source.GetCatalogAsync(2025))).Kind);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrUnreportedDefaultSelectorCannotBeConstructedForAnotherYear(bool unreported)
    {
        var metadata = JsonNode.Parse(Metadata(2026))!.AsObject();
        if (unreported) metadata["currDetaShtmFg"] = "U000300099"; else metadata.Remove("currDetaShtmFg");
        var handler = new Capture(metadata.ToJsonString()); using var source = Client(handler);
        Assert.Equal(SourceFailureKind.ParseChanged,
            (await Assert.ThrowsAsync<SnuSourceException>(() => source.GetCatalogAsync(2025))).Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DuplicateDefaultOptionsAreAChangedSourceContractRatherThanAnUnhandledFailure()
    {
        var metadata = JsonNode.Parse(Metadata(2026))!.AsObject();
        var terms = metadata["SHTM"]!.AsArray();
        terms.Add(terms.Single(term => term!["cmmnCd"]!.GetValue<string>() == Winter)!.DeepClone());
        var handler = new Capture(metadata.ToJsonString()); using var source = Client(handler);
        Assert.Equal(SourceFailureKind.ParseChanged,
            (await Assert.ThrowsAsync<SnuSourceException>(() => source.GetCatalogAsync(2025))).Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DuplicateRawDefaultFieldsCannotChooseBetweenTwoReportedTermOptions()
    {
        foreach (var field in new[] { ("currShtmFg", "U000200001", "U000200002"), ("currDetaShtmFg", "U000300001", "U000300002") })
        {
            var metadata = Metadata(2026).Replace("\"" + field.Item1 + "\":\"" + field.Item3 + "\"",
                "\"" + field.Item1 + "\":\"" + field.Item2 + "\",\"" + field.Item1 + "\":\"" + field.Item3 + "\"", StringComparison.Ordinal);
            var handler = new Capture(metadata, Metadata(2025)); using var source = Client(handler);
            Assert.Equal(SourceFailureKind.ParseChanged,
                (await Assert.ThrowsAsync<SnuSourceException>(() => source.GetCatalogAsync(2025))).Kind);
            Assert.Single(handler.Requests);
        }
    }

    [Fact]
    public async Task DuplicateTermListsAreNotAUniqueReportedCatalog()
    {
        var metadata = Metadata(2026);
        var terms = JsonNode.Parse(metadata)!["SHTM"]!.ToJsonString();
        metadata = metadata.Replace("\"SHTM\":" + terms, "\"SHTM\":[],\"SHTM\":" + terms, StringComparison.Ordinal);
        var handler = new Capture(metadata, Metadata(2025)); using var source = Client(handler);
        Assert.Equal(SourceFailureKind.ParseChanged,
            (await Assert.ThrowsAsync<SnuSourceException>(() => source.GetCatalogAsync(2025))).Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ExplicitTermKeepsTheExactSelectionWithoutBootstrapOrCurrentYearSubstitution()
    {
        var handler = new Capture(Metadata(2025)); using var source = Client(handler);
        var catalog = await source.GetCatalogAsync(2025, Regular, "300", "326");
        Assert.Equal(2025, catalog.Year);
        Assert.Equal("srchOpenSchyy=2025&srchOpenShtm=" + Regular + "&openUpDeptCd=300&openDeptCd=326",
            Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task ExplicitSelectionCannotRelabelAMismatchingResponseYear()
    {
        var handler = new Capture(Metadata(2026)); using var source = Client(handler);
        Assert.Equal(SourceFailureKind.ParseChanged,
            (await Assert.ThrowsAsync<SnuSourceException>(() => source.GetCatalogAsync(2025, Regular))).Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CatalogWireMatchesTheDocumentedAnonymousAjaxProtocol()
    {
        var handler = new Capture(Metadata(2026)); using var source = Client(handler);
        await source.GetCatalogAsync(2026);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("XMLHttpRequest", request.RequestedWith);
        Assert.Equal("application/json, text/javascript, */*; q=0.01", request.Accept);
        Assert.Equal("https://sugang.snu.ac.kr/sugang/co/co010.action", request.Referrer);
        Assert.Equal("application/x-www-form-urlencoded; charset=UTF-8", request.ContentType);
        Assert.Equal("SNU-Watcher/0.1 (anonymous public course monitor)", request.UserAgent);
        Assert.Null(request.Authorization); Assert.Null(request.Cookie);
    }

    private static string Metadata(int openYear)
    {
        var value = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "catalog.json")))!.AsObject();
        value["openSchyy"] = openYear; value["currSchyy"] = 2026;
        value["currShtmFg"] = "U000200002"; value["currDetaShtmFg"] = "U000300002";
        return value.ToJsonString();
    }
    private static SnuCourseSource Client(Capture handler) => SnuCourseSource.CreateForTesting(new HttpClient(handler), new(TimeSpan.Zero));
    private sealed record WireRequest(string Body, string? RequestedWith, string Accept, string? Referrer,
        string? ContentType, string UserAgent, string? Authorization, string? Cookie);
    private sealed class Capture(params string[] responses) : HttpMessageHandler
    {
        public List<WireRequest> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(await request.Content!.ReadAsStringAsync(cancellationToken),
                request.Headers.TryGetValues("X-Requested-With", out var xhr) ? string.Join("", xhr) : null,
                request.Headers.Accept.ToString(), request.Headers.Referrer?.ToString(), request.Content.Headers.ContentType?.ToString(),
                request.Headers.UserAgent.ToString(), request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Cookie", out var cookie) ? string.Join("", cookie) : null));
            return new(HttpStatusCode.Created) { RequestMessage = request,
                Content = new StringContent(responses[Math.Min(Requests.Count - 1, responses.Length - 1)], Encoding.UTF8, "application/json") };
        }
    }
}
