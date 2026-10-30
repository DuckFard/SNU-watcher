using System.Net;
using System.Text;
using SnuWatcher.Infrastructure.Snu;
using Xunit;

namespace SnuWatcher.Integration.Tests;

public sealed class SnuSourceTests
{
    private static readonly SourceTerm Regular = new(2026, "U000200002U000300001", "2학기", "2nd Semester");

    [Fact]
    public async Task DiscoversSeasonalTermsWithoutChangingTheirSourceSelectors()
    {
        using var source = Client(Json(Fixture("catalog.json")));
        var catalog = await source.GetCatalogAsync(2026);
        Assert.Equal("U000200002U000300002", catalog.Terms.Single(t => t.Name == "겨울학기").Selector);
        Assert.Equal("U000200002U000300001", catalog.Terms.Single(t => t.Name == "2학기").Selector);
        Assert.Equal("326", catalog.Departments.Single(d => d.Name == "통계학과").Code);
    }

    [Fact]
    public async Task PostsEachSearchFilterAndPreservesExactSectionIdentityAcrossPages()
    {
        var handler = new Responses(Html(Fixture("search-page1.html")), Html(Fixture("search-page2.html")));
        using var source = Client(handler);
        var result = await source.SearchAsync(new(Regular, "F32.102", "통계", "교수", "200", "326", ""));
        Assert.True(result.IsComplete);
        var section = result.Sections.Single(s => s.Identity.Section == "012");
        Assert.Equal("U000300001", section.Identity.DetailSemesterCode);
        Assert.Equal("000", section.Identity.SubsubjectCode);
        Assert.Equal(1, section.AvailableSeats);
        Assert.Contains("srchProfNm=", handler.Bodies[0]);
        Assert.Contains("srchOpenDeptCd=326", handler.Bodies[0]);
        Assert.Contains("pageNo=2", handler.Bodies[1]);
    }

    [Fact]
    public async Task LaterPageFailureReturnsPartialCoverageInsteadOfAnEmptySuccess()
    {
        using var source = Client(Html(Fixture("search-page1.html")), new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var result = await source.SearchAsync(new(Regular, CourseCode: "F32.102"));
        Assert.False(result.IsComplete);
        Assert.Single(result.Sections);
        Assert.Equal(SourceFailureKind.Unavailable, Assert.Single(result.Failures).Kind);
    }

    [Fact]
    public async Task ParsesRichDetailsOnlyForTheRequestedFullSourceIdentity()
    {
        using var source = Client(Json(Fixture("details.json")));
        var key = new SourceSectionIdentity(2026, "U000200002", "U000300001", "F32.102", "012", "000");
        var detail = await source.GetDetailsAsync(key);
        Assert.Equal(key, detail.Identity);
        Assert.Equal("Statistics", detail.EnglishName);
        Assert.Equal(2, detail.Meetings.Count);
        Assert.Equal("26-B102", detail.Meetings[0].Room);
        Assert.Equal("선수과목 확인", detail.Restrictions);
    }

    [Fact]
    public async Task LegacyDetailEndpointServesJsonAsHtmlButHtmlMaintenanceStillFails()
    {
        using var source = Client(Html(Fixture("details.json")));
        Assert.Equal(Key(), (await source.GetDetailsAsync(Key())).Identity);
        using var maintenance = Client(Html("<html><title>Maintenance</title></html>"));
        Assert.Equal(SourceFailureKind.ParseChanged, (await Assert.ThrowsAsync<SnuSourceException>(() => maintenance.GetDetailsAsync(Key()))).Kind);
    }

    [Fact]
    public async Task RejectsDetailsFromAnotherSection()
    {
        using var source = Client(Json(Fixture("details.json")));
        var key = new SourceSectionIdentity(2026, "U000200002", "U000300001", "F32.102", "001", "000");
        var error = await Assert.ThrowsAsync<SnuSourceException>(() => source.GetDetailsAsync(key));
        Assert.Equal(SourceFailureKind.ParseChanged, error.Kind);
    }

    [Theory]
    [InlineData("<html><title>Maintenance</title></html>")]
    [InlineData("<html><title>검색결과</title><div class='course-info-item'>changed markup</div></html>")]
    public async Task RejectsMaintenanceAndUnparseableCards(string body)
    {
        using var source = Client(Html(body));
        await Assert.ThrowsAsync<SnuSourceException>(() => source.SearchAsync(new(Regular, CourseCode: "F32.102")));
    }

    [Fact]
    public async Task MalformedCountsRemainUnknownRatherThanBecomingAnOpening()
    {
        using var source = Client(Html(Fixture("search-page2.html").Replace("39/40", "?/40")));
        var result = await source.SearchAsync(new(Regular, CourseCode: "F32.102"));
        Assert.Null(Assert.Single(result.Sections).AvailableSeats);
    }

    [Fact]
    public async Task QueryNeverSubstitutesACourseFromAnotherTerm()
    {
        using var source = Client(Html(Fixture("search-page2.html").Replace("U000300001", "U000300002")));
        var error = await Assert.ThrowsAsync<SnuSourceException>(() => source.SearchAsync(new(Regular, CourseCode: "F32.102")));
        Assert.Equal(SourceFailureKind.ParseChanged, error.Kind);
    }

    [Fact]
    public async Task RejectsWrongOriginAndRedirectInsteadOfFollowingIt()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new("https://example.test/steal");
        using var source = Client(response);
        var error = await Assert.ThrowsAsync<SnuSourceException>(() => source.GetCatalogAsync(2026));
        Assert.Equal(SourceFailureKind.AccessRestricted, error.Kind);
    }

    [Fact]
    public async Task RejectsAResponseAlreadyRedirectedToAnotherOrigin()
    {
        var response = Json(Fixture("catalog.json"));
        response.RequestMessage = new(HttpMethod.Get, "https://example.test/sugang/cc/cc100ajax.action");
        using var source = Client(response);
        await Assert.ThrowsAsync<SnuSourceException>(() => source.GetCatalogAsync(2026));
    }

    [Theory]
    [InlineData("60")]
    [InlineData("Wed, 28 Oct 2026 05:01:00 GMT")]
    public async Task RateLimitCarriesIntegerOrHttpDateCooldown(string retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        using var source = Client(response);
        var error = await Assert.ThrowsAsync<SnuSourceException>(() => source.GetCatalogAsync(2026));
        Assert.Equal(SourceFailureKind.RateLimited, error.Kind);
        Assert.NotNull(error.RetryAfter);
    }

    [Fact]
    public async Task RejectsOversizedResponsesAndUnexpectedContentTypes()
    {
        using var source = Client(Json(new string('x', 2000)), maxBytes: 1024);
        var error = await Assert.ThrowsAsync<SnuSourceException>(() => source.GetCatalogAsync(2026));
        Assert.Equal(SourceFailureKind.ResponseTooLarge, error.Kind);
        using var wrongType = Client(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data", Encoding.UTF8, "application/octet-stream") });
        await Assert.ThrowsAsync<SnuSourceException>(() => wrongType.GetCatalogAsync(2026));
    }

    [Fact]
    public async Task CancellationIsPropagatedAndNotReportedAsSourceOutage()
    {
        using var source = Client(Json(Fixture("catalog.json")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetCatalogAsync(2026, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task BoundsAdvertisedPaginationAndDoesNotPollEveryPageOnBroadSearch()
    {
        using var source = Client(Html(Fixture("search-page1.html").Replace("fnGotoPage('2')", "fnGotoPage('999')")));
        var result = await source.SearchAsync(new(Regular, CourseCode: "F32.102"));
        Assert.False(result.IsComplete);
        Assert.Equal(SourceFailureKind.LimitReached, Assert.Single(result.Failures).Kind);
    }

    [Fact]
    public void SourceIdentityRejectsInvalidAndIncompleteIdentifiers()
    {
        Assert.Throws<ArgumentException>(() => new SourceSectionIdentity(2026, "U000200002", "", "F32.102", "12", "000"));
        Assert.Throws<ArgumentException>(() => new SourceSectionIdentity(2026, "U000200002", "U000300001", "<script>", "012", "000"));
    }

    [Theory]
    [InlineData("-1/40")]
    [InlineData("39/40.5")]
    [InlineData("1,000/40")]
    [InlineData("999999999999999999999/40")]
    public async Task NeverInfersSeatsFromAMalformedCountToken(string count)
    {
        using var source = Client(Html(Fixture("search-page2.html").Replace("39/40 (40)", count)));
        Assert.Null(Assert.Single((await source.SearchAsync(new(Regular))).Sections).AvailableSeats);
    }

    [Fact]
    public async Task PartialRateLimitRetainsCooldownForTheSharedScheduler()
    {
        var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        limited.Headers.TryAddWithoutValidation("Retry-After", "120");
        using var source = Client(Html(Fixture("search-page1.html")), limited);
        var result = await source.SearchAsync(new(Regular));
        Assert.Equal(TimeSpan.FromSeconds(120), Assert.Single(result.Failures).RetryAfter);
    }

    [Fact]
    public async Task ShortLegacyTermMustNotMatchBothRegularAndWinter()
    {
        using var source = Client(Html(Fixture("search-page2.html")));
        var error = await Assert.ThrowsAsync<SnuSourceException>(() => source.SearchAsync(new(new(2026, "U000200002", "legacy"))));
        Assert.Equal(SourceFailureKind.ParseChanged, error.Kind);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"SHTM\":[]}")]
    [InlineData("{\"SHTM\":[null]}")]
    [InlineData("{\"SHTM\":[3]}")]
    [InlineData("{\"SHTM\":[{\"cmmnCd\":\"bad<>\",\"korNm\":\"term\"}]}")]
    [InlineData("{\"SHTM\":[{\"cmmnCd\":\"code\"}]}")]
    public async Task ChangedCatalogContractsAreTypedFailures(string body)
    {
        using var source = Client(Json(body));
        var error = await Assert.ThrowsAsync<SnuSourceException>(() => source.GetCatalogAsync(2026));
        Assert.Equal(SourceFailureKind.ParseChanged, error.Kind);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"deptCd\":\"invalid<>\",\"deptKorNm\":\"name\"}]")]
    [InlineData("[{\"deptCd\":\"326\"}]")]
    public async Task ChangedDepartmentContractsAreTypedFailures(string departments)
    {
        using var source = Client(Json("{\"SHTM\":[{\"cmmnCd\":\"term\",\"korNm\":\"name\"}],\"DEPT\":" + departments + "}"));
        Assert.Equal(SourceFailureKind.ParseChanged, (await Assert.ThrowsAsync<SnuSourceException>(() => source.GetCatalogAsync(2026))).Kind);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"LISTTAB01\":[]}")]
    [InlineData("{\"LISTTAB01\":{\"openSchyy\":\"bad\"}}")]
    public async Task ChangedDetailContractsAreTypedFailures(string body)
    {
        using var source = Client(Json(body));
        await Assert.ThrowsAsync<SnuSourceException>(() => source.GetDetailsAsync(Key()));
    }

    [Theory]
    [InlineData("\"ltType\":[\"이론\",\"이론\"]", "\"ltType\":[]")]
    [InlineData("\"ltType\":[\"이론\",\"이론\"]", "\"ltType\":[null,null]")]
    [InlineData("\"sbjtNm\":\"통계학\"", "\"sbjtNm\":null")]
    public async Task MissingDetailMetadataAndMismatchedMeetingsFailSafely(string original, string replacement)
    {
        using var source = Client(Json(Fixture("details.json").Replace(original, replacement)));
        await Assert.ThrowsAsync<SnuSourceException>(() => source.GetDetailsAsync(Key()));
    }

    [Theory]
    [InlineData(401, SourceFailureKind.AccessRestricted)]
    [InlineData(403, SourceFailureKind.AccessRestricted)]
    [InlineData(500, SourceFailureKind.Unavailable)]
    public async Task HttpFailuresAreClassified(int status, SourceFailureKind expected)
    {
        using var source = Client(new HttpResponseMessage((HttpStatusCode)status));
        Assert.Equal(expected, (await Assert.ThrowsAsync<SnuSourceException>(() => source.GetCatalogAsync(2026))).Kind);
    }

    [Fact]
    public async Task StreamingBoundAppliesWithoutADeclaredLength()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(new string('x', 2048)) };
        using var source = Client(response, 1024);
        Assert.Equal(SourceFailureKind.ResponseTooLarge, (await Assert.ThrowsAsync<SnuSourceException>(() => source.GetCatalogAsync(2026))).Kind);
    }

    [Fact]
    public async Task RequestDeadlineAlsoBoundsAStalledBodyAfterHeadersArrive()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledContent() };
        using var source = new SnuCourseSource(new HttpClient(new Responses(response)), new(TimeSpan.Zero, RequestTimeout: TimeSpan.FromMilliseconds(20)));
        var error = await Assert.ThrowsAsync<SnuSourceException>(() => source.GetCatalogAsync(2026).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(SourceFailureKind.Unavailable, error.Kind);
    }

    [Fact]
    public async Task NetworkAndTransportTimeoutsAreTypedWithoutLeakingMessages()
    {
        using var network = new SnuCourseSource(new HttpClient(new ThrowingHandler(new HttpRequestException("private transport detail"))));
        var error = await Assert.ThrowsAsync<SnuSourceException>(() => network.GetCatalogAsync(2026));
        Assert.DoesNotContain("private", error.Message);
        using var timeout = new SnuCourseSource(new HttpClient(new ThrowingHandler(new TaskCanceledException())));
        Assert.Equal(SourceFailureKind.Unavailable, (await Assert.ThrowsAsync<SnuSourceException>(() => timeout.GetCatalogAsync(2026))).Kind);
    }

    [Fact]
    public async Task SearchInputsAreBoundedBeforeAnyRequest()
    {
        using var source = Client();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => source.GetCatalogAsync(1800));
        await Assert.ThrowsAsync<ArgumentException>(() => source.GetCatalogAsync(2026, "<bad>"));
        await Assert.ThrowsAsync<ArgumentException>(() => source.SearchAsync(new(Regular, Name: new string('x', 201))));
        await Assert.ThrowsAsync<ArgumentException>(() => source.SearchAsync(new(Regular, Instructor: "bad\ntext")));
        await Assert.ThrowsAsync<ArgumentException>(() => source.SearchAsync(new(new(2026, "", "term"))));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnuCourseSource(new HttpClient(), new(TimeSpan.Zero, 0)));
    }

    [Fact]
    public async Task ExplicitEmptyResultAndMissingCountsDoNotImplyAnOpening()
    {
        using var empty = Client(Html("<html><title>Search Results</title><body>No courses found</body></html>"));
        Assert.Empty((await empty.SearchAsync(new(Regular))).Sections);
        using var missing = Client(Html(Fixture("search-page2.html").Replace("39/40 (40)", "not available")));
        Assert.Null(Assert.Single((await missing.SearchAsync(new(Regular))).Sections).Registered);
    }

    [Fact]
    public async Task ObservedKoreanZeroResultBannerWithAnEmptyListIsACompleteEmptySearch()
    {
        using var source = Client(Html(Fixture("search-empty.html")));
        var result = await source.SearchAsync(new(Regular, Name: "NoSuchCourse"));
        Assert.True(result.IsComplete);
        Assert.Empty(result.Sections);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task ZeroResultTextWithoutTheCourseListDoesNotTurnAGateIntoSuccess()
    {
        using var source = Client(Html(Fixture("search-empty.html").Replace("<div class=\"course-info-list\"></div>", "")));
        Assert.Equal(SourceFailureKind.ParseChanged, (await Assert.ThrowsAsync<SnuSourceException>(() => source.SearchAsync(new(Regular)))).Kind);
    }

    [Fact]
    public async Task SearchUnavailableTextIsNotAnEmptySearchResult()
    {
        using var source = Client(Html("<html><title>검색결과</title><body><div class='course-info-list'></div>현재 검색할 수 없습니다.</body></html>"));
        Assert.Equal(SourceFailureKind.ParseChanged, (await Assert.ThrowsAsync<SnuSourceException>(() => source.SearchAsync(new(Regular)))).Kind);
    }

    [Fact]
    public async Task EnglishLabelsAndOptionalNumericMetadataArePreserved()
    {
        using var english = Client(Html(Fixture("search-page2.html").Replace("수강신청인원/정원(재학생)", "Enrollment/Quota")));
        Assert.Equal("Enrollment/Quota", Assert.Single((await english.SearchAsync(new(Regular))).Sections).CountLabel);
        using var detail = Client(Json(Fixture("details.json").Replace("\"openPnt\":3", "\"openPnt\":null").Replace("\"tlsnAplyCapaCnt\":40", "\"tlsnAplyCapaCnt\":null")));
        var result = await detail.GetDetailsAsync(Key());
        Assert.Null(result.Credits);
        Assert.Null(result.Capacity);
    }

    [Fact]
    public async Task MissingNamesAndInvalidIdentityRowsAreRejected()
    {
        foreach (var html in new[] { Fixture("search-page2.html").Replace("value=\"012\"", "value=\"12\""), Fixture("search-page2.html").Replace("name=\"sbjtNm\" value=\"통계학\"", "name=\"sbjtNm\" value=\"\"") })
        {
            using var source = Client(Html(html));
            await Assert.ThrowsAsync<SnuSourceException>(() => source.SearchAsync(new(Regular)));
        }
    }

    private static SourceSectionIdentity Key() => new(2026, "U000200002", "U000300001", "F32.102", "012", "000");

    private sealed class ThrowingHandler(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromException<HttpResponseMessage>(error);
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly string body;
        public UnknownLengthContent(string body) { this.body = body; Headers.ContentType = new("application/json"); }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(Encoding.UTF8.GetBytes(body)).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(body)));
    }

    private sealed class StalledContent : HttpContent
    {
        public StalledContent() { Headers.ContentType = new("application/json"); }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new StalledStream());
    }

    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    private static HttpResponseMessage Html(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "text/html") };
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static SnuCourseSource Client(params HttpResponseMessage[] responses) => Client(new Responses(responses));
    private static SnuCourseSource Client(HttpResponseMessage response, int maxBytes) => Client(new Responses(response), maxBytes);
    private static SnuCourseSource Client(Responses handler, int maxBytes = 2_000_000) => new(new HttpClient(handler), new SnuSourceOptions(TimeSpan.Zero, 25, maxBytes));

    private sealed class Responses(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> remaining = new(responses);
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            var response = remaining.Dequeue();
            response.RequestMessage ??= request;
            return response;
        }
    }
}
