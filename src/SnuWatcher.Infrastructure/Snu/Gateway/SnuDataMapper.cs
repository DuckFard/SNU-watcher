using SnuWatcher.Application.Sources;
using SnuWatcher.Core;

namespace SnuWatcher.Infrastructure.Snu.Gateway;

/// <summary>
/// Pure mapping of bounded verified source facts. No request, cache lookup, timestamp generation or
/// availability policy occurs here. Callers resolve catalog terms before dispatch and supply the exact
/// dispatched query; source receipt times remain distinct from a fallback failed-outcome time.
/// </summary>
public static class SnuDataMapper
{
    public static SourceRead<CourseCatalogData> Catalog(CatalogQuery query, SourceCatalog source, DateTimeOffset completedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            if (source is null || source.Year != query.Year) return Failed<CourseCatalogData>(completedAtUtc);
            var data = new CourseCatalogData(query, source.Terms.Select(Term), source.Departments.Select(Department));
            if (query.TermSelector is not null && !data.Terms.Any(term => term.Selector == query.TermSelector))
                return new(SourceReadStatus.Failed, null, [new(SourceProblemKind.TermUnavailable, "term_unconfirmed")], completedAtUtc);
            return new(SourceReadStatus.Complete, data, [], completedAtUtc);
        }
        catch (Exception error) when (InvalidShape(error)) { return Failed<CourseCatalogData>(completedAtUtc); }
    }

    /// <summary>
    /// Invalid identities, missing page receipt times and invalid display rows make coverage partial.
    /// Invalid individual count fields become null, retaining the row for Core's unusable-count policy.
    /// outcomeAtUtc is used only when no trustworthy query completion time is available for a failure;
    /// it is never used as a row receipt time or as a replacement for a supplied completion instant.
    /// </summary>
    public static SourceRead<CourseSearchData> Search(CourseSearchQuery query, SourceSearchQuery dispatched,
        SourceSearchResult source, DateTimeOffset outcomeAtUtc)
    {
        ArgumentNullException.ThrowIfNull(query);
        var completedAt = source?.CompletedAtUtc ?? outcomeAtUtc;
        if (!SnuIdentityMapper.Matches(query, dispatched) || source is null) return Failed<CourseSearchData>(completedAt);
        try
        {
            var rows = source.Sections.Take(SourceLimits.MaxRows + 1).ToArray();
            var failures = source.Failures.Take(SourceLimits.MaxProblems + 1).ToArray();
            if (rows.Length > SourceLimits.MaxRows || failures.Length > SourceLimits.MaxProblems
                || source.PagesFetched is < 0 or > SourceLimits.MaxPages) return Failed<CourseSearchData>(completedAt);
            var problems = Problems(failures);
            if (source.CompletedAtUtc is null) return Failed<CourseSearchData>(completedAt, WithParseProblem(problems));
            if (source.PagesFetched == 0)
                return Failed<CourseSearchData>(completedAt, problems.Count > 0 && rows.Length == 0 ? problems : WithParseProblem(problems));
            return SearchPages(query, source, rows, problems, completedAt);
        }
        catch (Exception error) when (InvalidShape(error)) { return Failed<CourseSearchData>(completedAt); }
    }

    public static SourceRead<CourseDetails> Details(SectionKey key, SourceDetails source, DateTimeOffset completedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(key);
        try
        {
            if (source is null || SnuIdentityMapper.ToSectionKey(source.Identity) != key) return Failed<CourseDetails>(completedAtUtc);
            var data = new CourseDetails(key, source.Name, source.EnglishName, source.Instructor, source.EnglishInstructor,
                source.Department, source.Credits, Count(source.Capacity), source.InstructionLanguage, source.Restrictions,
                source.Meetings.Select(Meeting));
            return new(SourceReadStatus.Complete, data, [], completedAtUtc);
        }
        catch (Exception error) when (InvalidShape(error)) { return Failed<CourseDetails>(completedAtUtc); }
    }

    private static SourceRead<CourseSearchData> SearchPages(CourseSearchQuery query, SourceSearchResult source,
        SourceSection[] rows, IReadOnlyList<SourceProblem> sourceProblems, DateTimeOffset completedAt)
    {
        Dictionary<SectionKey, CourseSearchRow> valid = [];
        HashSet<SectionKey> seen = [];
        var invalid = false;
        foreach (var raw in rows)
        {
            if (!TryRow(raw, query, out var row)) { invalid = true; continue; }
            if (!seen.Add(row!.Key))
            {
                valid.Remove(row.Key); // An unverified repeated identity cannot pick a convenient count pair.
                invalid = true;
            }
            else valid.Add(row.Key, row);
        }
        var problems = invalid || (!source.IsComplete && sourceProblems.Count == 0)
            ? WithParseProblem(sourceProblems) : sourceProblems;
        var data = new CourseSearchData(query, valid.Values, source.PagesFetched);
        return new(problems.Count == 0 ? SourceReadStatus.Complete : SourceReadStatus.Partial, data, problems, completedAt);
    }

    private static bool TryRow(SourceSection? source, CourseSearchQuery query, out CourseSearchRow? row)
    {
        row = null;
        if (source?.ObservedAtUtc is not { } observedAt) return false;
        try
        {
            var key = SnuIdentityMapper.ToSectionKey(source.Identity);
            if (key.Year != query.Year || key.SemesterCode + key.DetailSemesterCode != query.TermSelector) return false;
            row = new(key, source.Name, source.Instructor, source.Department, source.Schedule, Count(source.Registered),
                Count(source.Capacity), Count(source.TotalRegistered), source.CountLabel, observedAt);
            return true;
        }
        catch (Exception error) when (InvalidShape(error)) { return false; }
    }

    private static CatalogTerm Term(SourceTerm source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new(source.Year, source.Selector, source.Name, source.EnglishName);
    }

    private static DepartmentOption Department(SourceDepartment source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new(source.Code, source.Name, source.EnglishName, source.Level);
    }

    private static CourseMeeting Meeting(SourceMeeting source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new(source.Time, source.Type, source.Room);
    }

    private static int? Count(int? value) => value is >= 0 and <= SourceLimits.MaxCount ? value : null;

    private static IReadOnlyList<SourceProblem> Problems(IEnumerable<SourceFailure> failures) => failures
        .Select(failure => failure is null ? ParseProblem() : SourceFailureMapper.Map(failure))
        .GroupBy(problem => (problem.Kind, problem.DiagnosticCode))
        .Select(group => group.OrderByDescending(problem => problem.RetryAfter).First()).ToArray();

    private static IReadOnlyList<SourceProblem> WithParseProblem(IReadOnlyList<SourceProblem> problems) =>
        problems.Any(problem => problem.Kind == SourceProblemKind.ParseChanged) ? problems : [.. problems, ParseProblem()];

    private static SourceProblem ParseProblem() => new(SourceProblemKind.ParseChanged, "source_parse_changed");

    private static SourceRead<T> Failed<T>(DateTimeOffset at, IEnumerable<SourceProblem>? problems = null) where T : class =>
        new(SourceReadStatus.Failed, null, problems ?? [ParseProblem()], at);

    private static bool InvalidShape(Exception error) => error is ArgumentException or InvalidOperationException;
}
