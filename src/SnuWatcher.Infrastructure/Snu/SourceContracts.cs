using System.Text.RegularExpressions;

namespace SnuWatcher.Infrastructure.Snu;

/// <summary>Opaque site selector and source-provided labels for one academic term.</summary>
public sealed record SourceTerm(int Year, string Selector, string Name, string? EnglishName = null);

/// <summary>Full raw identity from a search card. No aliases between subterms are assumed.</summary>
public sealed record SourceSectionIdentity
{
    public SourceSectionIdentity(int year, string semesterCode, string detailSemesterCode, string courseCode, string section, string subsubjectCode)
    {
        if (year is < 2013 or > 2200 || !Valid(semesterCode) || !Valid(detailSemesterCode) ||
            !Valid(courseCode) || !Regex.IsMatch(section ?? "", @"^\d{3}$") || !Valid(subsubjectCode))
            throw new ArgumentException("A complete source section identity is required.");
        Year = year;
        SemesterCode = semesterCode;
        DetailSemesterCode = detailSemesterCode;
        CourseCode = courseCode;
        Section = section!;
        SubsubjectCode = subsubjectCode;
    }

    public int Year { get; }
    public string SemesterCode { get; }
    public string DetailSemesterCode { get; }
    public string CourseCode { get; }
    public string Section { get; }
    public string SubsubjectCode { get; }
    internal static bool Valid(string? value) => value is { Length: > 0 and <= 80 } && Regex.IsMatch(value, @"^[A-Za-z0-9.]+$");
}

public sealed record SourceDepartment(string Code, string Name, string? EnglishName, string Level);
public sealed record SourceCatalog(int Year, IReadOnlyList<SourceTerm> Terms, IReadOnlyList<SourceDepartment> Departments);
public sealed record SourceSearchQuery(SourceTerm Term, string? CourseCode = null, string? Name = null,
    string? Instructor = null, string? UpDepartment = null, string? Department = null, string? Major = null);

/// <summary>Counts retain their public quota label; apparent seats do not imply personal eligibility.</summary>
public sealed record SourceSection(SourceSectionIdentity Identity, string Name, string? Instructor,
    string? Department, string? Schedule, int? Registered, int? Capacity, int? TotalRegistered, string CountLabel)
{
    public int? AvailableSeats => Registered.HasValue && Capacity.HasValue ? Math.Max(0, Capacity.Value - Registered.Value) : null;
}

public sealed record SourceMeeting(string Time, string? Type, string? Room);
public sealed record SourceDetails(SourceSectionIdentity Identity, string Name, string? EnglishName,
    string? Instructor, string? EnglishInstructor, string? Department, decimal? Credits,
    int? Capacity, string? InstructionLanguage, string? Restrictions, IReadOnlyList<SourceMeeting> Meetings);
public sealed record SourceSearchResult(IReadOnlyList<SourceSection> Sections, bool IsComplete,
    IReadOnlyList<SourceFailure> Failures, int PagesFetched);
public sealed record SourceFailure(SourceFailureKind Kind, string Message, TimeSpan? RetryAfter = null);
public enum SourceFailureKind { Unavailable, AccessRestricted, RateLimited, ParseChanged, ResponseTooLarge, LimitReached }

/// <summary>A typed source failure; never contains raw response bodies, cookies, or credentials.</summary>
public sealed class SnuSourceException(SourceFailureKind kind, string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public SourceFailureKind Kind { get; } = kind;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>Resource bounds. Zero spacing is available to deterministic offline tests only.</summary>
public sealed record SnuSourceOptions(TimeSpan RequestSpacing, int MaxPages = 25, int MaxResponseBytes = 2_000_000,
    TimeSpan? RequestTimeout = null)
{
    public static SnuSourceOptions Default { get; } = new(TimeSpan.FromSeconds(2));
}
