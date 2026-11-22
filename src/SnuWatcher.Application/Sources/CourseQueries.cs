namespace SnuWatcher.Application.Sources;

/// <summary>Exact metadata-cache identity; source selectors are opaque and never inferred from labels.</summary>
public sealed record CatalogQuery
{
    public CatalogQuery(int year, string? termSelector = null, string? upDepartment = null, string? department = null)
    {
        Year = SourceValidation.Year(year);
        TermSelector = SourceValidation.OptionalToken(termSelector, nameof(termSelector));
        UpDepartment = SourceValidation.OptionalToken(upDepartment, nameof(upDepartment));
        Department = SourceValidation.OptionalToken(department, nameof(department));
    }

    public int Year { get; }
    public string? TermSelector { get; }
    public string? UpDepartment { get; }
    public string? Department { get; }
}

/// <summary>
/// Logical coalescing identity: exact year, raw catalog selector, every normalized filter and the
/// optional result page. Purpose, labels, watch scope and timestamps do not participate. Empty optional
/// filters become null; user text loses only outer whitespace while case and identifier spelling remain exact.
/// Without a page, the source reads every result page within its bound, so a filter is required. With a
/// page, exactly that one source page is read, as the registration site itself does; an unfiltered paged
/// query lists every course in the term one page at a time.
/// </summary>
public sealed record CourseSearchQuery
{
    public CourseSearchQuery(int year, string termSelector, string? courseCode = null, string? name = null,
        string? instructor = null, string? upDepartment = null, string? department = null, int? page = null,
        string? grade = null, string? level = null)
    {
        Year = SourceValidation.Year(year);
        TermSelector = SourceValidation.Token(termSelector, nameof(termSelector));
        CourseCode = SourceValidation.OptionalToken(courseCode, nameof(courseCode));
        Name = SourceValidation.FilterText(name, nameof(name));
        Instructor = SourceValidation.FilterText(instructor, nameof(instructor));
        UpDepartment = SourceValidation.OptionalToken(upDepartment, nameof(upDepartment));
        Department = SourceValidation.OptionalToken(department, nameof(department));
        Grade = SourceValidation.OptionalToken(grade, nameof(grade));
        Level = SourceValidation.OptionalToken(level, nameof(level));
        Page = page is null or (>= 1 and <= SourceLimits.MaxBrowsePage) ? page
            : throw new ArgumentOutOfRangeException(nameof(page), "Result pages start at 1 and stay within the browse bound.");
        if (Page is null && CourseCode is null && Name is null && Instructor is null && UpDepartment is null
            && Department is null && Grade is null && Level is null)
            throw new ArgumentException("Select a meaningful search filter, or request one result page, before requesting course results.");
    }

    public int Year { get; }
    public string TermSelector { get; }
    public string? CourseCode { get; }
    public string? Name { get; }
    public string? Instructor { get; }
    public string? UpDepartment { get; }
    public string? Department { get; }
    /// <summary>One source result page, or null to read every page within the source bound.</summary>
    public int? Page { get; }
    /// <summary>Source grade option (1 to 5), carried unchanged.</summary>
    public string? Grade { get; }
    /// <summary>Source degree-program option (for example undergraduate or master's), carried unchanged.</summary>
    public string? Level { get; }

    /// <summary>The same filters for another single result page.</summary>
    public CourseSearchQuery ForPage(int page) =>
        new(Year, TermSelector, CourseCode, Name, Instructor, UpDepartment, Department, page, Grade, Level);
}
