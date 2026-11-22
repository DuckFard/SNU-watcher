using System.Collections.Immutable;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Sources;

/// <summary>
/// One exact source row. Gateways normalize malformed, negative or out-of-bound raw counts to null,
/// matching the SNU parser, so a bad row remains available for Core to classify as unusable. Zero quota
/// and over-capacity counts remain known facts. TotalRegistered is display data, never a quota substitute.
/// ObservedAtUtc is the original page-body receipt time, not final-query completion or cache-read time.
/// </summary>
public sealed record CourseSearchRow
{
    public CourseSearchRow(SectionKey key, string name, string? instructor, string? department, string? schedule,
        int? registered, int? capacity, int? totalRegistered, string? quotaLabel, DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(key);
        Key = key;
        Name = SourceValidation.Display(name, SourceLimits.MaxShortDisplay, nameof(name), requireText: true);
        Instructor = SourceValidation.OptionalDisplay(instructor, SourceLimits.MaxShortDisplay, nameof(instructor));
        Department = SourceValidation.OptionalDisplay(department, SourceLimits.MaxShortDisplay, nameof(department));
        Schedule = SourceValidation.OptionalDisplay(schedule, SourceLimits.MaxLongDisplay, nameof(schedule));
        Registered = SourceValidation.Count(registered, nameof(registered));
        Capacity = SourceValidation.Count(capacity, nameof(capacity));
        TotalRegistered = SourceValidation.Count(totalRegistered, nameof(totalRegistered));
        QuotaLabel = SourceValidation.OptionalDisplay(quotaLabel, SourceLimits.MaxShortDisplay, nameof(quotaLabel));
        ObservedAtUtc = observedAtUtc.ToUniversalTime();
    }

    public SectionKey Key { get; }
    public string Name { get; }
    public string? Instructor { get; }
    public string? Department { get; }
    public string? Schedule { get; }
    public int? Registered { get; }
    public int? Capacity { get; }
    public int? TotalRegistered { get; }
    public string? QuotaLabel { get; }
    public DateTimeOffset ObservedAtUtc { get; }

    private readonly string? classification;
    private readonly decimal? credits;

    /// <summary>The source's own program and course-type tags (for example "[학사] [전선]"); display only.</summary>
    public string? Classification
    {
        get => classification;
        init => classification = SourceValidation.OptionalDisplay(value, SourceLimits.MaxShortDisplay, nameof(Classification));
    }

    /// <summary>Source-reported credits, when the row states them.</summary>
    public decimal? Credits
    {
        get => credits;
        init => credits = value is null or (>= 0 and <= SourceLimits.MaxCredits) ? value
            : throw new ArgumentOutOfRangeException(nameof(Credits), "Credits must be null or between zero and 100.");
    }
}

/// <summary>
/// Verified fetched-page data. A complete empty search still fetched at least one page. A paged query also
/// carries the source's total result count and last page when the response states them.
/// </summary>
public sealed record CourseSearchData
{
    public CourseSearchData(CourseSearchQuery query, IEnumerable<CourseSearchRow> rows, int pagesFetched,
        int? totalCount = null, int? lastPage = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        Query = query;
        Rows = SourceValidation.Snapshot(rows, SourceLimits.MaxRows, nameof(rows));
        if (pagesFetched is < 1 or > SourceLimits.MaxPages) throw new ArgumentOutOfRangeException(nameof(pagesFetched));
        if (Rows.DistinctBy(row => row.Key).Count() != Rows.Length)
            throw new ArgumentException("Resolve repeated identities before publishing source rows.", nameof(rows));
        if (totalCount is < 0 or > SourceLimits.MaxBrowsePage * 100) throw new ArgumentOutOfRangeException(nameof(totalCount));
        if (lastPage is < 1 or > SourceLimits.MaxBrowsePage) throw new ArgumentOutOfRangeException(nameof(lastPage));
        PagesFetched = pagesFetched;
        TotalCount = totalCount;
        LastPage = lastPage;
    }

    public CourseSearchQuery Query { get; }
    public ImmutableArray<CourseSearchRow> Rows { get; }
    public int PagesFetched { get; }
    /// <summary>Source-reported number of matching courses, when stated.</summary>
    public int? TotalCount { get; }
    /// <summary>Source-reported last result page, when stated.</summary>
    public int? LastPage { get; }
}
