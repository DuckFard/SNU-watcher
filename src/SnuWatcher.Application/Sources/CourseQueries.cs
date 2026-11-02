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
/// Logical coalescing identity: exact year, raw catalog selector and every normalized filter.
/// Purpose, labels, watch scope and timestamps do not participate. Empty optional filters become null;
/// user text loses only outer whitespace while case and identifier spelling remain exact.
/// </summary>
public sealed record CourseSearchQuery
{
    public CourseSearchQuery(int year, string termSelector, string? courseCode = null, string? name = null,
        string? instructor = null, string? upDepartment = null, string? department = null)
    {
        Year = SourceValidation.Year(year);
        TermSelector = SourceValidation.Token(termSelector, nameof(termSelector));
        CourseCode = SourceValidation.OptionalToken(courseCode, nameof(courseCode));
        Name = SourceValidation.FilterText(name, nameof(name));
        Instructor = SourceValidation.FilterText(instructor, nameof(instructor));
        UpDepartment = SourceValidation.OptionalToken(upDepartment, nameof(upDepartment));
        Department = SourceValidation.OptionalToken(department, nameof(department));
        if (CourseCode is null && Name is null && Instructor is null && UpDepartment is null && Department is null)
            throw new ArgumentException("Select a meaningful search filter before requesting course results.");
    }

    public int Year { get; }
    public string TermSelector { get; }
    public string? CourseCode { get; }
    public string? Name { get; }
    public string? Instructor { get; }
    public string? UpDepartment { get; }
    public string? Department { get; }
}
