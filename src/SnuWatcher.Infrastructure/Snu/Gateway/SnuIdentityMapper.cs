using System.Diagnostics.CodeAnalysis;
using SnuWatcher.Application.Sources;
using SnuWatcher.Core;

namespace SnuWatcher.Infrastructure.Snu.Gateway;

/// <summary>Exact six-part mapping and source-catalog term resolution, without aliases or inferred code widths.</summary>
public static class SnuIdentityMapper
{
    public static SectionKey ToSectionKey(SourceSectionIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new(identity.Year, identity.SemesterCode, identity.DetailSemesterCode,
            identity.CourseCode, identity.Section, identity.SubsubjectCode);
    }

    public static SourceSectionIdentity ToSourceIdentity(SectionKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new(key.Year, key.SemesterCode, key.DetailSemesterCode, key.CourseCode, key.Section, key.SubsubjectCode);
    }

    /// <summary>
    /// Requires a term actually present in the supplied catalog. Production callers obtain this catalog
    /// from the source; this pure boundary validates agreement and cannot attest a caller's catalog origin.
    /// Department filters are carried unchanged; unverified major/union-department semantics are not added.
    /// </summary>
    public static bool TryCreateSearchQuery(CourseSearchQuery query, CourseCatalogData catalog,
        [NotNullWhen(true)] out SourceSearchQuery? sourceQuery)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(catalog);
        var term = catalog.Query.Year == query.Year
            ? catalog.Terms.SingleOrDefault(term => term.Year == query.Year && term.Selector == query.TermSelector)
            : null;
        sourceQuery = term is null ? null : new(new(term.Year, term.Selector, term.Name, term.EnglishName),
            query.CourseCode, query.Name, query.Instructor, query.UpDepartment, query.Department);
        return sourceQuery is not null;
    }

    internal static bool Matches(CourseSearchQuery query, SourceSearchQuery? dispatched) =>
        dispatched?.Term is { } term && term.Year == query.Year && term.Selector == query.TermSelector
        && dispatched.CourseCode == query.CourseCode && dispatched.Name == query.Name
        && dispatched.Instructor == query.Instructor && dispatched.UpDepartment == query.UpDepartment
        && dispatched.Department == query.Department && dispatched.Major is null;
}
