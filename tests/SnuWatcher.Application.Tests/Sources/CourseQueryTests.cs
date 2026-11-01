using SnuWatcher.Application.Sources;
using Xunit;

namespace SnuWatcher.Application.Tests.Sources;

public sealed class CourseQueryTests
{
    private const string Term = "U000200002U000300002";

    [Fact]
    public void OptionalEmptyFiltersNormalizeOnceAndEveryMeaningfulFieldParticipatesInEquality()
    {
        var query = new CourseSearchQuery(2026, Term, "F32.102", " 통계학 ", " ", "", null);
        var same = new CourseSearchQuery(2026, Term, "F32.102", "통계학");
        Assert.Equal(same, query);
        Assert.Equal(same.GetHashCode(), query.GetHashCode());
        Assert.Single(new HashSet<CourseSearchQuery> { query, same });
        Assert.Null(query.Instructor);
        Assert.Null(query.UpDepartment);
        Assert.Null(query.Department);

        Assert.NotEqual(query, new(2025, Term, "F32.102", "통계학"));
        Assert.NotEqual(query, new(2026, "opaqueAlternate", "F32.102", "통계학"));
        Assert.NotEqual(query, new(2026, Term, "f32.102", "통계학"));
        Assert.NotEqual(query, new(2026, Term, "F32.102", "통계"));
        Assert.NotEqual(query, new(2026, Term, "F32.102", "통계학", "Kim"));
        Assert.NotEqual(query, new(2026, Term, "F32.102", "통계학", upDepartment: "100"));
        Assert.NotEqual(query, new(2026, Term, "F32.102", "통계학", department: "200"));
    }

    [Fact]
    public void CatalogQueryIsAnExactNormalizedMetadataCacheKey()
    {
        Assert.Equal(new CatalogQuery(2026), new CatalogQuery(2026, "", " ", null));
        var query = new CatalogQuery(2026, Term, "100", "200");
        Assert.NotEqual(query, new(2025, Term, "100", "200"));
        Assert.NotEqual(query, new(2026, "other", "100", "200"));
        Assert.NotEqual(query, new(2026, Term, "101", "200"));
        Assert.NotEqual(query, new(2026, Term, "100", "201"));
    }

    [Theory]
    [InlineData(2012)]
    [InlineData(2201)]
    public void UnsupportedSourceYearIsRejected(int year)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CatalogQuery(year));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CourseSearchQuery(year, Term, "F32.102"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CatalogTerm(year, Term, "2학기", null));
    }

    [Theory]
    [InlineData("", "F32.102")]
    [InlineData("term\n", "F32.102")]
    [InlineData("term", "F32.102\n")]
    [InlineData("term", "F32_102")]
    [InlineData("term", "통계학")]
    [InlineData("term", " F32.102")]
    public void IdentifiersRemainExactAsciiTokens(string term, string course)
    {
        Assert.Throws<ArgumentException>(() => new CourseSearchQuery(2026, term, course));
    }

    [Fact]
    public void ASearchNeedsAMeaningfulBoundedFilterAndCannotHideControlCharactersInEmptyText()
    {
        Assert.Throws<ArgumentException>(() => new CourseSearchQuery(2026, Term));
        Assert.Throws<ArgumentException>(() => new CourseSearchQuery(2026, Term, name: " ", instructor: ""));
        Assert.Throws<ArgumentException>(() => new CourseSearchQuery(2026, Term, name: "\t"));
        Assert.Throws<ArgumentException>(() => new CourseSearchQuery(2026, Term, instructor: "Kim\nPark"));
        Assert.Throws<ArgumentException>(() => new CourseSearchQuery(2026, Term, name: new('x', 201)));
        Assert.Throws<ArgumentException>(() => new CatalogQuery(2026, department: new('1', 81)));
        Assert.Throws<ArgumentException>(() => new CatalogQuery(2026, termSelector: new(' ', 81)));
        Assert.Equal(new string('x', 200), new CourseSearchQuery(2013, Term, name: new('x', 200)).Name);
        Assert.Equal("001", new CourseSearchQuery(2200, Term, department: "001").Department);
    }
}
