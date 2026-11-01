using System.Collections.Immutable;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Sources;

public sealed class SourceDataTests
{
    private static readonly SectionKey Key = new(2026, "U000200002", "U000300002", "F32.102", "012", "00");
    private static readonly CourseSearchQuery Query = new(2026, "U000200002U000300002", "F32.102");
    private static readonly DateTimeOffset Received = new(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public void CatalogRetainsOpaqueSelectorsAndSnapshotsCollectionsWithoutInventingTermAliases()
    {
        var terms = new List<CatalogTerm> { new(2026, "rawOpaque01", "2학기\n정규", "Fall\tterm") };
        var departments = new List<DepartmentOption> { new("001", "통계학과", null, "UP_DEPT"), new("001", "통계학과", null, "DEPT") };
        var catalog = new CourseCatalogData(new(2026), terms, departments);
        terms.Clear();
        departments.Clear();
        Assert.Equal("rawOpaque01", Assert.Single(catalog.Terms).Selector);
        Assert.Equal("2학기\n정규", catalog.Terms[0].Name);
        Assert.Equal(2, catalog.Departments.Length);
        Assert.Equal("001", catalog.Departments[0].Code);
        Assert.Throws<NotSupportedException>(() => ((IList<CatalogTerm>)catalog.Terms).Clear());
    }

    [Fact]
    public void CatalogRejectsMissingDuplicateOrCrossYearTermsAndDuplicateDepartmentAssociations()
    {
        var term = new CatalogTerm(2026, "opaqueTerm", "Term", null);
        var department = new DepartmentOption("001", "Statistics", null, "DEPT");
        Assert.Throws<ArgumentException>(() => new CourseCatalogData(new(2026), [], []));
        Assert.Throws<ArgumentException>(() => new CourseCatalogData(new(2026), [term, term], []));
        Assert.Throws<ArgumentException>(() => new CourseCatalogData(new(2025), [term], []));
        Assert.Throws<ArgumentException>(() => new CourseCatalogData(new(2026), [term], [department, department]));
        Assert.Throws<ArgumentException>(() => new CourseCatalogData(new(2026), Enumerable.Repeat(term, SourceLimits.MaxTerms + 1), []));
        Assert.Throws<ArgumentException>(() => new CourseCatalogData(new(2026), [term], Enumerable.Repeat(department, SourceLimits.MaxDepartments + 1)));
        Assert.Throws<ArgumentException>(() => new CatalogTerm(2026, "", "Term", null));
        Assert.Throws<ArgumentException>(() => new DepartmentOption("001\n", "Statistics", null, "DEPT"));
    }

    [Fact]
    public void SearchRowsRetainExactKeyRawNullableCountsAndOriginalReceiptTime()
    {
        var row = Row(120, 0);
        var rows = new List<CourseSearchRow> { row };
        var data = new CourseSearchData(Query, rows, 2);
        rows.Clear();
        Assert.Same(row, Assert.Single(data.Rows));
        Assert.Same(Key, row.Key);
        Assert.Equal("012", row.Key.Section);
        Assert.Equal("00", row.Key.SubsubjectCode);
        Assert.Equal(120, row.Registered);
        Assert.Equal(0, row.Capacity);
        Assert.Equal(100_000, row.TotalRegistered);
        Assert.Equal("unverified quota\nlabel", row.QuotaLabel);
        Assert.Equal(Received.ToUniversalTime(), row.ObservedAtUtc);
        Assert.Equal(TimeSpan.Zero, row.ObservedAtUtc.Offset);
        Assert.Null(Row(null, null).Registered);
        Assert.Null(Row(null, null).Capacity);
        Assert.Throws<NotSupportedException>(() => ((IList<CourseSearchRow>)data.Rows).Clear());
    }

    [Fact]
    public void SearchCoverageIsBoundedAndDuplicateRowsCannotSilentlyReplaceCounts()
    {
        var row = Row(119, 120);
        Assert.Empty(new CourseSearchData(Query, [], 1).Rows);
        Assert.Equal(100, new CourseSearchData(Query, [], 100).PagesFetched);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CourseSearchData(Query, [], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CourseSearchData(Query, [], 101));
        Assert.Throws<ArgumentException>(() => new CourseSearchData(Query, [row, row], 1));
        Assert.Throws<ArgumentException>(() => new CourseSearchData(Query, default(ImmutableArray<CourseSearchRow>), 1));
        Assert.Throws<ArgumentException>(() => new CourseSearchData(Query, [null!], 1));
        Assert.Throws<ArgumentException>(() => new CourseSearchData(Query, Enumerable.Repeat(row, SourceLimits.MaxRows + 1), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Row(-1, 120));
        Assert.Throws<ArgumentOutOfRangeException>(() => Row(0, 100_001));
    }

    [Fact]
    public void DetailsSnapshotMeetingsAndPreserveMultilineSourceDisplayText()
    {
        var meetings = new List<CourseMeeting> { new("월\t14:00\n수 14:00", "강의", "건물 001\n방 012") };
        var details = Details(meetings, "제한\n둘째 줄\t설명");
        meetings.Clear();
        Assert.Equal("제한\n둘째 줄\t설명", details.Restrictions);
        Assert.Equal("월\t14:00\n수 14:00", Assert.Single(details.Meetings).Time);
        Assert.Equal(3.5m, details.Credits);
        Assert.Equal(0, details.Capacity);
        Assert.Throws<NotSupportedException>(() => ((IList<CourseMeeting>)details.Meetings).Clear());
        Assert.Empty(Details([], null).Meetings);
    }

    [Fact]
    public void MetadataBoundsRejectOversizedOrUnsafeDisplayWithoutTruncatingIt()
    {
        Assert.Throws<ArgumentException>(() => Row(0, 1, name: "\n\t"));
        Assert.Throws<ArgumentException>(() => Row(0, 1, name: "Name\0private"));
        Assert.Throws<ArgumentException>(() => Row(0, 1, name: new('x', SourceLimits.MaxShortDisplay + 1)));
        Assert.Throws<ArgumentException>(() => new CourseMeeting(new('x', SourceLimits.MaxLongDisplay + 1), null, null));
        Assert.Throws<ArgumentException>(() => Details([], new('x', SourceLimits.MaxRestrictions + 1)));
        Assert.Throws<ArgumentException>(() => Details(Enumerable.Repeat(new CourseMeeting("", null, null), SourceLimits.MaxMeetings + 1), null));
        Assert.Throws<ArgumentOutOfRangeException>(() => Details([], null, credits: -1m));
        Assert.Throws<ArgumentOutOfRangeException>(() => Details([], null, capacity: -1));
        Assert.Equal(new string('x', SourceLimits.MaxShortDisplay), Row(0, 0, name: new('x', SourceLimits.MaxShortDisplay)).Name);
    }

    private static CourseSearchRow Row(int? registered, int? capacity, string name = "통계학") =>
        new(Key, name, "김\n이", "통계학과", "월\t14:00", registered, capacity, 100_000, "unverified quota\nlabel", Received);

    private static CourseDetails Details(IEnumerable<CourseMeeting> meetings, string? restrictions, decimal? credits = 3.5m, int? capacity = 0) =>
        new(Key, "통계학", "Statistics", "김", "Kim", "통계학과", credits, capacity, "Korean", restrictions, meetings);
}
