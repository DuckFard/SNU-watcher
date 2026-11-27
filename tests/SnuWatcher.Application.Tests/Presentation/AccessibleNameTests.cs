using SnuWatcher.Application.Presentation;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Presentation;

/// <summary>List items are named after ToString(); a record's default would be a dump of every property.</summary>
public sealed class AccessibleNameTests
{
    private static readonly SectionKey Key = new(2026, "U000200002", "U000300002", "F32.102", "013", "00");
    private static readonly DateTimeOffset Received = new(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public void JoinsOnlyTheNonEmptyPartsTrimmed()
    {
        Assert.Equal("F32.102 013, Statistics", AccessibleName.Of(" F32.102 013 ", null, "", "  ", "Statistics"));
        Assert.Equal("", AccessibleName.Of());
        Assert.Throws<ArgumentNullException>(() => AccessibleName.Of(null!));
    }

    [Fact]
    public void LongTextIsCutToTheLimitWithAnEllipsis()
    {
        var name = AccessibleName.Of(new string('가', 600));
        Assert.Equal(AccessibleName.MaxLength, name.Length);
        Assert.EndsWith("…", name, StringComparison.Ordinal);
        Assert.Equal(AccessibleName.MaxLength, AccessibleName.Of(new string('a', AccessibleName.MaxLength)).Length);
        Assert.DoesNotContain("…", AccessibleName.Of(new string('a', AccessibleName.MaxLength)), StringComparison.Ordinal);
    }

    [Fact]
    public void SourceItemsReadAsWhatTheListShows()
    {
        var row = new CourseSearchRow(Key, "통계학", "Kim", null, null, 39, 40, 40, null, Received);
        Assert.Equal("F32.102 013, 통계학", row.ToString());
        Assert.Equal("월(14:00~15:15), 강의, 25-101", new CourseMeeting("월(14:00~15:15)", "강의", "25-101").ToString());
        Assert.Equal("월(14:00~15:15)", new CourseMeeting("월(14:00~15:15)", null, null).ToString());
        Assert.Equal("Statistics", new DepartmentOption("001", "Statistics", "Statistics (en)", "DEPT").ToString());
    }

    [Fact]
    public void ALongCourseNameStaysWithinTheLimit()
    {
        var row = new CourseSearchRow(Key, new string('n', SourceLimits.MaxShortDisplay), null, null, null, null, null, null, null, Received);
        var name = row.ToString();
        Assert.StartsWith("F32.102 013, nnn", name, StringComparison.Ordinal);
        Assert.Equal(AccessibleName.MaxLength, name.Length);
    }
}
