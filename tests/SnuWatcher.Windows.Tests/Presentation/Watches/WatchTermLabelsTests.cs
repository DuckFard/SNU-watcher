using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Application.Presentation.Terms;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;
using SnuWatcher.Windows.Presentation.Watches;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Watches;

public sealed class WatchTermLabelsTests
{
    private static readonly AcademicTermIdentity Winter =
        new(new SectionKey(2026, "U000200002", "U000300002", "101.424", "001", "0"));

    private static readonly CatalogTerm CurrentWinter = new(2026, "U000200002U000300002", "겨울학기", "Winter Semester");

    private static TermHintCacheSnapshot Hints(int year, params CachedTermHint[] terms) =>
        new(1, [new CachedTermYear(year, 1, DateTimeOffset.UnixEpoch, terms)]);

    [Fact]
    public void AWatchInTheTermJustReadIsNamedFromThatTerm()
    {
        Assert.Equal("겨울학기", WatchTermLabels.Find(Winter, CurrentWinter, null, english: false));
        Assert.Equal("Winter Semester", WatchTermLabels.Find(Winter, CurrentWinter, null, english: true));
    }

    [Fact]
    public void AnEarlierTermIsNamedFromTheCachedHintsOfItsYear()
    {
        var spring = new AcademicTermIdentity(new SectionKey(2026, "U000200001", "U000300001", "101.424", "001", "0"));
        var hints = Hints(2026, new CachedTermHint("U000200001U000300001", "1학기", "1st Semester"),
            new CachedTermHint("U000200002U000300002", "겨울학기", "Winter Semester"));

        Assert.Equal("1학기", WatchTermLabels.Find(spring, CurrentWinter, hints, english: false));
        Assert.Equal("1st Semester", WatchTermLabels.Find(spring, CurrentWinter, hints, english: true));
    }

    [Fact]
    public void EnglishFallsBackToTheSourceNameWhenTheSourceGivesNoEnglishName()
    {
        var current = new CatalogTerm(2026, "U000200002U000300002", "겨울학기", null);

        Assert.Equal("겨울학기", WatchTermLabels.Find(Winter, current, null, english: true));
    }

    [Fact]
    public void ATermWithNoSourceNameStaysUnnamed()
    {
        var otherYear = Hints(2025, new CachedTermHint("U000200002U000300002", "겨울학기", "Winter Semester"));
        var otherSelector = new CatalogTerm(2026, "U000200001U000300001", "1학기", "1st Semester");

        Assert.Null(WatchTermLabels.Find(Winter, null, null, english: false));
        Assert.Null(WatchTermLabels.Find(Winter, otherSelector, otherYear, english: false));
    }

    [Fact]
    public void TheTermIsRequired() =>
        Assert.Throws<ArgumentNullException>(() => WatchTermLabels.Find(null!, CurrentWinter, null, english: false));
}
