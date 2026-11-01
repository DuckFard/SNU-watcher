using System.Collections.Immutable;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Sources;

namespace SnuWatcher.Application.Presentation.Terms;

/// <summary>Cached display data, deliberately a different type from authoritative CatalogTerm.</summary>
public sealed record CachedTermHint
{
    public CachedTermHint(string selector, string name, string? englishName)
    {
        Selector = SourceValidation.Token(selector, nameof(selector));
        Name = SourceValidation.Display(name, SourceLimits.MaxShortDisplay, nameof(name), requireText: true);
        EnglishName = SourceValidation.OptionalDisplay(englishName, SourceLimits.MaxShortDisplay, nameof(englishName));
    }
    public string Selector { get; }
    public string Name { get; }
    public string? EnglishName { get; }
}

public sealed record CachedTermYear
{
    public CachedTermYear(int year, long cacheRevision, DateTimeOffset fetchedAtUtc, IEnumerable<CachedTermHint> terms)
    {
        Year = SourceValidation.Year(year);
        CacheRevision = PersistenceValidation.Positive(cacheRevision, nameof(cacheRevision));
        Terms = PersistenceValidation.Snapshot(terms, SourceLimits.MaxTerms, nameof(terms));
        if (Terms.IsEmpty || Terms.DistinctBy(term => term.Selector).Count() != Terms.Length)
            throw new ArgumentException("Cached terms must be nonempty and have unique exact selectors.", nameof(terms));
        FetchedAtUtc = fetchedAtUtc.ToUniversalTime();
    }
    public int Year { get; }
    public long CacheRevision { get; }
    /// <summary>Original complete catalog receipt time, including metadata-cache hits; never a new local save time.</summary>
    public DateTimeOffset FetchedAtUtc { get; }
    public ImmutableArray<CachedTermHint> Terms { get; }
}

public sealed record TermHintCacheSnapshot
{
    public const int MaximumYears = 4;
    public TermHintCacheSnapshot(long revision, IEnumerable<CachedTermYear> years)
    {
        Revision = PersistenceValidation.Nonnegative(revision, nameof(revision));
        Years = PersistenceValidation.Snapshot(years, MaximumYears, nameof(years));
        if ((revision == 0) != Years.IsEmpty || Years.DistinctBy(year => year.Year).Count() != Years.Length
            || Years.DistinctBy(year => year.CacheRevision).Count() != Years.Length
            || !Years.IsEmpty && Years.Max(year => year.CacheRevision) != revision)
            throw new ArgumentException("Cache years must have unique identities/write orders and include the latest global revision.", nameof(years));
    }
    public long Revision { get; }
    public ImmutableArray<CachedTermYear> Years { get; }
}

/// <summary>Capture expected revision before querying. A conflict skips caching rather than reissuing against a newer revision.</summary>
public sealed record ReplaceTermHintsCommand
{
    public ReplaceTermHintsCommand(long expectedCacheRevision, int year, DateTimeOffset fetchedAtUtc, IEnumerable<CatalogTerm> terms)
    {
        ExpectedCacheRevision = PersistenceValidation.Nonnegative(expectedCacheRevision, nameof(expectedCacheRevision));
        Year = SourceValidation.Year(year);
        Terms = PersistenceValidation.Snapshot(terms, SourceLimits.MaxTerms, nameof(terms));
        if (Terms.IsEmpty || Terms.Any(term => term.Year != year) || Terms.DistinctBy(term => term.Selector).Count() != Terms.Length)
            throw new ArgumentException("Replacement needs unique complete-catalog terms from the selected year.", nameof(terms));
        FetchedAtUtc = fetchedAtUtc.ToUniversalTime();
    }
    public long ExpectedCacheRevision { get; }
    public int Year { get; }
    public DateTimeOffset FetchedAtUtc { get; }
    public ImmutableArray<CatalogTerm> Terms { get; }
}

public enum TermHintWriteStatus { Applied, NoChange, VersionConflict }

public sealed record TermHintWriteResult
{
    public TermHintWriteResult(TermHintWriteStatus status, TermHintCacheSnapshot currentSnapshot)
    {
        Status = PersistenceValidation.Defined(status, nameof(status));
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        if (status != TermHintWriteStatus.VersionConflict && currentSnapshot.Revision == 0)
            throw new ArgumentException("Accepted or unchanged term hints require existing cached data.", nameof(currentSnapshot));
        CurrentSnapshot = currentSnapshot;
    }
    public TermHintWriteStatus Status { get; }
    public TermHintCacheSnapshot CurrentSnapshot { get; }
}
