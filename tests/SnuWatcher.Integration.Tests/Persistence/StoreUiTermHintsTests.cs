using SnuWatcher.Application.Presentation.Terms;
using SnuWatcher.Application.Sources;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreUiTermHintsTests
{
    private static ITermHintCache Cache(StoreFixture fixture) => (ITermHintCache)(object)fixture.Store;

    [Fact]
    public async Task CacheStartsEmptyAndStoresExactHistoricalCatalogTimeAndHostileDisplayTextLocally()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var empty = await Cache(fixture).ReadAsync();
        Assert.Equal(0, empty.Revision);
        Assert.Empty(empty.Years);
        var fetched = fixture.Clock.GetUtcNow().AddDays(-5);
        var title = "2학기 '); DROP TABLE Subscriptions;--\n\"<term>\"";
        var write = await Cache(fixture).TryReplaceYearAsync(new(0, 2026, fetched,
            [new CatalogTerm(2026, "U000200002.U000300002", title, "Autumn & winter")]));
        Assert.Equal(TermHintWriteStatus.Applied, write.Status);
        await fixture.ReopenAsync();
        var saved = Assert.Single((await Cache(fixture).ReadAsync()).Years);
        Assert.Equal(fetched, saved.FetchedAtUtc);
        Assert.Equal(title, Assert.Single(saved.Terms).Name);
        Assert.Equal(0L, StoreUiPreferencesTests.Scalar(fixture, "SELECT issuedOrdinal+catalogRevision FROM StoreMetadata;"));
        foreach (var table in new[] { "Subscriptions", "Members", "SectionState", "Events", "Outbox", "SourcePolicies" })
            Assert.Equal(0L, StoreUiPreferencesTests.Scalar(fixture, "SELECT count(*) FROM " + table + ";"));
    }

    [Fact]
    public async Task OldestWriteRevisionIsEvictedDespiteClockRollbackAndEvictedYearCannotReuseItsOldRevision()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        long revision = 0;
        foreach (var year in new[] { 2026, 2025, 2024, 2023, 2022 })
        {
            fixture.Clock.Advance(TimeSpan.FromDays(-1));
            var result = await Cache(fixture).TryReplaceYearAsync(Command(revision, year, fixture.Clock.GetUtcNow()));
            Assert.Equal(TermHintWriteStatus.Applied, result.Status);
            revision = result.CurrentSnapshot.Revision;
        }
        var cached = await Cache(fixture).ReadAsync();
        Assert.Equal(5, cached.Revision);
        Assert.Equal(4, cached.Years.Length);
        Assert.DoesNotContain(cached.Years, year => year.Year == 2026);
        var stale = await Cache(fixture).TryReplaceYearAsync(Command(1, 2026, fixture.Clock.GetUtcNow()));
        Assert.Equal(TermHintWriteStatus.VersionConflict, stale.Status);
        Assert.Equal(5, stale.CurrentSnapshot.Revision);
        var restored = await Cache(fixture).TryReplaceYearAsync(Command(5, 2026, fixture.Clock.GetUtcNow()));
        Assert.Equal(6, Assert.Single(restored.CurrentSnapshot.Years, x => x.Year == 2026).CacheRevision);
        Assert.DoesNotContain(restored.CurrentSnapshot.Years, year => year.Year == 2025);
    }

    [Fact]
    public async Task SameYearFactsAndTimeReturnNoChangeButChangedOriginalTimeCreatesANewVersion()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var at = fixture.Clock.GetUtcNow().AddDays(-10);
        await Cache(fixture).TryReplaceYearAsync(Command(0, 2026, at));
        var unchanged = await Cache(fixture).TryReplaceYearAsync(Command(1, 2026, at));
        Assert.Equal(TermHintWriteStatus.NoChange, unchanged.Status);
        Assert.Equal(1, unchanged.CurrentSnapshot.Revision);
        var changed = await Cache(fixture).TryReplaceYearAsync(Command(1, 2026, at.AddSeconds(-1)));
        Assert.Equal(TermHintWriteStatus.Applied, changed.Status);
        Assert.Equal(2, changed.CurrentSnapshot.Revision);
        Assert.Equal(at.AddSeconds(-1), Assert.Single(changed.CurrentSnapshot.Years).FetchedAtUtc);
    }

    [Fact]
    public async Task TwoInstancesUseOneGlobalCompareAndSwapAcrossDifferentYears()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await using var second = await SqliteWatchStore.OpenAsync(new(fixture.DatabasePath), fixture.Clock);
        var results = await Task.WhenAll(Cache(fixture).TryReplaceYearAsync(Command(0, 2026, fixture.Clock.GetUtcNow())),
            ((ITermHintCache)(object)second).TryReplaceYearAsync(Command(0, 2025, fixture.Clock.GetUtcNow())));
        Assert.Single(results, x => x.Status == TermHintWriteStatus.Applied);
        Assert.Single(results, x => x.Status == TermHintWriteStatus.VersionConflict);
        var saved = await Cache(fixture).ReadAsync();
        Assert.Equal(1, saved.Revision);
        Assert.Single(saved.Years);
    }

    [Fact]
    public async Task LargestBoundedEscapedCatalogRoundTripsWithoutTruncation()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var label = new string('한', 512);
        var terms = Enumerable.Range(0, 128).Select(index => new CatalogTerm(2026, "Term." + index, label, label));
        var result = await Cache(fixture).TryReplaceYearAsync(new(0, 2026, fixture.Clock.GetUtcNow(), terms));
        Assert.Equal(128, Assert.Single(result.CurrentSnapshot.Years).Terms.Length);
        await fixture.ReopenAsync();
        Assert.All(Assert.Single((await Cache(fixture).ReadAsync()).Years).Terms, term =>
        { Assert.Equal(label, term.Name); Assert.Equal(label, term.EnglishName); });
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":999,\"terms\":[]}")]
    public async Task CorruptCachedHintsDoNotBecomeAnEmptyCacheOrGetSilentlyOverwritten(string json)
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await Cache(fixture).TryReplaceYearAsync(Command(0, 2026, fixture.Clock.GetUtcNow()));
        StoreUiPreferencesTests.Write(fixture, "UPDATE UiTermHints SET hintsJson=$json;", ("$json", json));
        await Assert.ThrowsAnyAsync<Exception>(() => Cache(fixture).ReadAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => Cache(fixture).TryReplaceYearAsync(Command(1, 2026, fixture.Clock.GetUtcNow())));
        Assert.Equal(json, StoreUiPreferencesTests.Scalar(fixture, "SELECT hintsJson FROM UiTermHints;"));
        Assert.Equal(1L, StoreUiPreferencesTests.Scalar(fixture, "SELECT revision FROM UiTermCacheState;"));
    }

    private static ReplaceTermHintsCommand Command(long revision, int year, DateTimeOffset at) =>
        new(revision, year, at, [new CatalogTerm(year, "U000200002.U000300002", "2학기", "Second semester")]);
}
