using System.Collections.Immutable;
using System.Reflection;
using SnuWatcher.Application.Presentation.Terms;
using SnuWatcher.Application.Sources;
using Xunit;

namespace SnuWatcher.Application.Tests.Presentation.Terms;

public sealed class TermHintContractTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T01:00:00Z");

    [Fact]
    public void CachedHintsCopyDisplayTextAndTimeWithoutAcquiringCatalogAuthority()
    {
        var hint = new CachedTermHint("U000200002.U000300002", "2학기 \"정규\"\n표시\t", "Second semester");
        var terms = new List<CachedTermHint> { hint };
        var year = new CachedTermYear(2026, 3, Now.ToOffset(TimeSpan.FromHours(9)), terms);
        terms.Clear();
        Assert.Equal(2026, year.Year);
        Assert.Equal(3, year.CacheRevision);
        Assert.Equal(Now, year.FetchedAtUtc);
        Assert.Equal(TimeSpan.Zero, year.FetchedAtUtc.Offset);
        Assert.Same(hint, Assert.Single(year.Terms));
        Assert.Equal("U000200002.U000300002", hint.Selector);
        Assert.Equal("2학기 \"정규\"\n표시\t", hint.Name);
        Assert.Equal("Second semester", hint.EnglishName);
        Assert.False(typeof(CatalogTerm).IsAssignableFrom(typeof(CachedTermHint)));
        Assert.Null(new CachedTermHint("x", "학기", null).EnglishName);
        Assert.Throws<ArgumentException>(() => new CachedTermHint("x\n", "x", null));
        Assert.Throws<ArgumentException>(() => new CachedTermHint("x", "", null));
        Assert.Throws<ArgumentException>(() => new CachedTermHint("x", new string('x', 513), null));
        Assert.Throws<ArgumentException>(() => new CachedTermHint("x", "x", new string('x', 513)));
    }

    [Fact]
    public void GlobalCacheRevisionSurvivesYearEvictionAndUsesNoWallClockOrdering()
    {
        var years = new List<CachedTermYear> { Year(2023, 7, Now.AddDays(1)), Year(2024, 8, Now.AddDays(-1)), Year(2025, 9), Year(2026, 10) };
        var snapshot = new TermHintCacheSnapshot(10, years);
        years.Clear();
        Assert.Equal(10, snapshot.Revision);
        Assert.Equal(4, snapshot.Years.Length);
        Assert.Empty(new TermHintCacheSnapshot(0, []).Years);
        Assert.Throws<ArgumentException>(() => new TermHintCacheSnapshot(0, [Year(2026, 1)]));
        Assert.Throws<ArgumentException>(() => new TermHintCacheSnapshot(1, []));
        Assert.Throws<ArgumentException>(() => new TermHintCacheSnapshot(11, snapshot.Years));
        Assert.Throws<ArgumentException>(() => new TermHintCacheSnapshot(9, snapshot.Years));
        Assert.Throws<ArgumentException>(() => new TermHintCacheSnapshot(11, snapshot.Years.Append(Year(2027, 11))));
        Assert.Throws<ArgumentException>(() => new TermHintCacheSnapshot(10, [Year(2026, 9), Year(2026, 10)]));
        Assert.Throws<ArgumentException>(() => new TermHintCacheSnapshot(10, [Year(2025, 10), Year(2026, 10)]));
        Assert.Throws<ArgumentException>(() => new TermHintCacheSnapshot(0, default(ImmutableArray<CachedTermYear>)));
        Assert.Throws<ArgumentNullException>(() => new TermHintCacheSnapshot(0, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TermHintCacheSnapshot(-1, []));
    }

    [Fact]
    public void ReplacementAcceptsOnlyAnExplicitCompleteCatalogYearWithUniqueBoundedTerms()
    {
        var terms = new List<CatalogTerm> { new(2026, "a", "1학기", null), new(2026, "b", "2학기", "Second") };
        var command = new ReplaceTermHintsCommand(17, 2026, Now, terms);
        terms.Clear();
        Assert.Equal(17, command.ExpectedCacheRevision);
        Assert.Equal(2026, command.Year);
        Assert.Equal(Now, command.FetchedAtUtc);
        Assert.Equal(2, command.Terms.Length);
        Assert.Throws<ArgumentException>(() => new ReplaceTermHintsCommand(0, 2025, Now, command.Terms));
        Assert.Throws<ArgumentException>(() => new ReplaceTermHintsCommand(0, 2026, Now, [command.Terms[0], command.Terms[0]]));
        Assert.Throws<ArgumentException>(() => new ReplaceTermHintsCommand(0, 2026, Now, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplaceTermHintsCommand(-1, 2026, Now, command.Terms));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplaceTermHintsCommand(0, 2012, Now, command.Terms));
        Assert.Throws<ArgumentNullException>(() => new ReplaceTermHintsCommand(0, 2026, Now, null!));
        Assert.Throws<ArgumentException>(() => new ReplaceTermHintsCommand(0, 2026, Now, default(ImmutableArray<CatalogTerm>)));
    }

    [Fact]
    public void AYearHasAtMost128NonemptyTermsWithNoNullOrDefaultAlias()
    {
        var max = Enumerable.Range(0, 128).Select(index => new CachedTermHint("term" + index, "학기", null)).ToArray();
        Assert.Equal(128, new CachedTermYear(2200, 1, Now, max).Terms.Length);
        Assert.Throws<ArgumentException>(() => new CachedTermYear(2026, 1, Now, max.Append(new("extra", "Extra", null))));
        Assert.Throws<ArgumentException>(() => new CachedTermYear(2026, 1, Now, [max[0], max[0]]));
        Assert.Throws<ArgumentException>(() => new CachedTermYear(2026, 1, Now, []));
        Assert.Throws<ArgumentException>(() => new CachedTermYear(2026, 1, Now, [null!]));
        Assert.Throws<ArgumentException>(() => new CachedTermYear(2026, 1, Now, default(ImmutableArray<CachedTermHint>)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CachedTermYear(2012, 1, Now, max));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CachedTermYear(2026, 0, Now, max));
    }

    [Fact]
    public void CacheResultsDistinguishConflictFromNoChangeWithoutInventingANewFetchTime()
    {
        var snapshot = new TermHintCacheSnapshot(7, [Year(2026, 7)]);
        var applied = new TermHintWriteResult(TermHintWriteStatus.Applied, snapshot);
        Assert.Equal(TermHintWriteStatus.Applied, applied.Status);
        Assert.Same(snapshot, applied.CurrentSnapshot);
        Assert.Same(snapshot, new TermHintWriteResult(TermHintWriteStatus.NoChange, snapshot).CurrentSnapshot);
        Assert.Equal(0, new TermHintWriteResult(TermHintWriteStatus.VersionConflict, new(0, [])).CurrentSnapshot.Revision);
        Assert.Throws<ArgumentException>(() => new TermHintWriteResult(TermHintWriteStatus.Applied, new(0, [])));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TermHintWriteResult((TermHintWriteStatus)99, snapshot));
        Assert.Throws<ArgumentNullException>(() => new TermHintWriteResult(TermHintWriteStatus.NoChange, null!));
    }

    [Fact]
    public void HintContractsHaveNoPublicSetters()
    {
        Type[] types = [typeof(CachedTermHint), typeof(CachedTermYear), typeof(TermHintCacheSnapshot), typeof(ReplaceTermHintsCommand), typeof(TermHintWriteResult)];
        Assert.All(types, type => Assert.All(type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            property => Assert.Null(property.SetMethod)));
    }

    private static CachedTermYear Year(int year, long revision, DateTimeOffset? time = null) => new(year, revision, time ?? Now, [new("x", "학기", null)]);
}
