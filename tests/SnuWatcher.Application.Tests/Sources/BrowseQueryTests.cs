using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Sources;

/// <summary>One-page browsing, the site's grade and program filters, stated totals and the reported current term.</summary>
public sealed class BrowseQueryTests
{
    private const string Term = "U000200002U000300002";
    private static readonly DateTimeOffset Received = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnUnfilteredQueryNeedsAPageWhileAnyFilterOrPageIsEnough()
    {
        Assert.Throws<ArgumentException>(() => new CourseSearchQuery(2026, Term));
        var browse = new CourseSearchQuery(2026, Term, page: 1);
        Assert.Equal(1, browse.Page);
        Assert.Null(browse.CourseCode);
        Assert.Equal("2", new CourseSearchQuery(2026, Term, grade: "2").Grade);
        Assert.Equal("C013300001", new CourseSearchQuery(2026, Term, level: "C013300001").Level);
        Assert.Null(new CourseSearchQuery(2026, Term, "F32.102", grade: " ", level: "").Grade);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CourseSearchQuery(2026, Term, page: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CourseSearchQuery(2026, Term, page: SourceLimits.MaxBrowsePage + 1));
        Assert.Throws<ArgumentException>(() => new CourseSearchQuery(2026, Term, page: 1, grade: "1 학년"));
    }

    [Fact]
    public void PageGradeAndProgramParticipateInIdentityAndForPageKeepsEveryFilter()
    {
        var query = new CourseSearchQuery(2026, Term, "F32.102", "통계", "Kim", "100", "200", 1, "3", "C013300001");
        Assert.NotEqual(query, query.ForPage(2));
        Assert.Equal(query, query.ForPage(2).ForPage(1));
        Assert.NotEqual(query, new CourseSearchQuery(2026, Term, "F32.102", "통계", "Kim", "100", "200", 1, "4", "C013300001"));
        Assert.NotEqual(query, new CourseSearchQuery(2026, Term, "F32.102", "통계", "Kim", "100", "200", 1, "3", "C013300002"));
        var next = query.ForPage(7);
        Assert.Equal((7, "F32.102", "통계", "Kim", "100", "200", "3", "C013300001"),
            (next.Page, next.CourseCode, next.Name, next.Instructor, next.UpDepartment, next.Department, next.Grade, next.Level));
    }

    [Fact]
    public void SearchDataKeepsStatedTotalsAndRejectsImpossibleOnes()
    {
        var query = new CourseSearchQuery(2026, Term, page: 1);
        var data = new CourseSearchData(query, [], 1, 279, 28);
        Assert.Equal((279, 28), (data.TotalCount, data.LastPage));
        Assert.Equal((null, null), (new CourseSearchData(query, [], 1).TotalCount, new CourseSearchData(query, [], 1).LastPage));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CourseSearchData(query, [], 1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CourseSearchData(query, [], 1, SourceLimits.MaxBrowsePage * 100 + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CourseSearchData(query, [], 1, null, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CourseSearchData(query, [], 1, null, SourceLimits.MaxBrowsePage + 1));
    }

    [Fact]
    public void RowTagsAndCreditsAreValidatedDisplayFacts()
    {
        var key = new SectionKey(2026, "U000200002", "U000300002", "F32.102", "012", "00");
        var row = new CourseSearchRow(key, "통계학", null, null, null, 39, 40, 40, null, Received) { Classification = "[학사] [전선]", Credits = 3 };
        Assert.Equal(("[학사] [전선]", 3m), (row.Classification, row.Credits));
        Assert.NotEqual(row, row with { Credits = 2 });
        Assert.Null((row with { Classification = null, Credits = null }).Classification);
        Assert.Throws<ArgumentException>(() => row with { Classification = "tag\u0001" });
        Assert.Throws<ArgumentOutOfRangeException>(() => row with { Credits = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => row with { Credits = SourceLimits.MaxCredits + 1 });
    }

    [Fact]
    public void TheReportedCurrentTermResolvesOnlyWithinItsOwnYear()
    {
        var terms = new List<CatalogTerm> { new(2026, Term, "겨울학기", "Winter Semester"), new(2026, "U000200001U000300001", "1학기", null) };
        var catalog = new CourseCatalogData(new(2026), terms, []) { CurrentYear = 2026, CurrentTermSelector = Term };
        Assert.Equal("겨울학기", catalog.CurrentTerm!.Name);
        Assert.Null((catalog with { CurrentYear = 2025 }).CurrentTerm);
        Assert.Null((catalog with { CurrentTermSelector = "U000200002U000300009" }).CurrentTerm);
        Assert.Null((catalog with { CurrentTermSelector = null }).CurrentTerm);
        Assert.Null(new CourseCatalogData(new(2026), terms, []).CurrentYear);
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog with { CurrentYear = 2012 });
        Assert.Throws<ArgumentException>(() => catalog with { CurrentTermSelector = "not a selector" });
    }
}
