using System.Net;
using System.Text;
using SnuWatcher.Infrastructure.Snu;
using Xunit;

namespace SnuWatcher.Integration.Tests;

public sealed class SnuSourceTokenValidationTests
{
    private static readonly SourceTerm Term = new(2026, "U000200002U000300001", "2학기");

    [Theory]
    [InlineData("semester")]
    [InlineData("detailSemester")]
    [InlineData("course")]
    [InlineData("section")]
    [InlineData("subsubject")]
    public void IdentityRejectsTrailingNewlineInsteadOfTreatingItAsTheEndOfAToken(string field)
    {
        Assert.Throws<ArgumentException>(() => new SourceSectionIdentity(2026,
            "U000200002" + (field == "semester" ? "\n" : ""),
            "U000300001" + (field == "detailSemester" ? "\n" : ""),
            "F32.102" + (field == "course" ? "\n" : ""),
            "012" + (field == "section" ? "\n" : ""),
            "000" + (field == "subsubject" ? "\n" : "")));
    }

    [Theory]
    [InlineData("term")]
    [InlineData("course")]
    [InlineData("upDepartment")]
    [InlineData("department")]
    [InlineData("major")]
    public async Task SearchRejectsTrailingNewlineBeforeDispatch(string field)
    {
        var handler = new RecordingHandler();
        using var source = SnuCourseSource.CreateForTesting(new HttpClient(handler), new(TimeSpan.Zero));
        var query = new SourceSearchQuery(field == "term" ? Term with { Selector = Term.Selector + "\n" } : Term,
            CourseCode: "F32.102" + (field == "course" ? "\n" : ""),
            UpDepartment: "200" + (field == "upDepartment" ? "\n" : ""),
            Department: "326" + (field == "department" ? "\n" : ""),
            Major: "001" + (field == "major" ? "\n" : ""));

        await Assert.ThrowsAsync<ArgumentException>(() => source.SearchAsync(query));
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("term")]
    [InlineData("upDepartment")]
    [InlineData("department")]
    public async Task CatalogRejectsTrailingNewlineBeforeDispatch(string field)
    {
        var handler = new RecordingHandler();
        using var source = SnuCourseSource.CreateForTesting(new HttpClient(handler), new(TimeSpan.Zero));

        await Assert.ThrowsAsync<ArgumentException>(() => source.GetCatalogAsync(2026,
            termSelector: Term.Selector + (field == "term" ? "\n" : ""),
            upDepartment: "200" + (field == "upDepartment" ? "\n" : ""),
            department: "326" + (field == "department" ? "\n" : "")));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public void ValidIdentityPreservesAsciiPunctuationCaseAndLeadingZeros()
    {
        var identity = new SourceSectionIdentity(2026, "U000200002", "U000300001", "F32.102L", "002", "000");

        Assert.Equal("F32.102L", identity.CourseCode);
        Assert.Equal("002", identity.Section);
        Assert.Equal("000", identity.SubsubjectCode);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            // A real dispatch is a test failure even when parsing would subsequently fail.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }
}
