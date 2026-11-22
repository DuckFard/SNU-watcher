using System.Collections.Immutable;

namespace SnuWatcher.Application.Sources;

/// <summary>A source-reported selector and labels; no regular or seasonal aliases are manufactured.</summary>
public sealed record CatalogTerm
{
    public CatalogTerm(int year, string selector, string name, string? englishName)
    {
        Year = SourceValidation.Year(year);
        Selector = SourceValidation.Token(selector, nameof(selector));
        Name = SourceValidation.Display(name, SourceLimits.MaxShortDisplay, nameof(name), requireText: true);
        EnglishName = SourceValidation.OptionalDisplay(englishName, SourceLimits.MaxShortDisplay, nameof(englishName));
    }

    public int Year { get; }
    public string Selector { get; }
    public string Name { get; }
    public string? EnglishName { get; }
}

public sealed record DepartmentOption
{
    public DepartmentOption(string code, string name, string? englishName, string level)
    {
        Code = SourceValidation.Token(code, nameof(code));
        Name = SourceValidation.Display(name, SourceLimits.MaxShortDisplay, nameof(name), requireText: true);
        EnglishName = SourceValidation.OptionalDisplay(englishName, SourceLimits.MaxShortDisplay, nameof(englishName));
        Level = SourceValidation.Level(level);
    }

    public string Code { get; }
    public string Name { get; }
    public string? EnglishName { get; }
    public string Level { get; }
}

public sealed record CourseCatalogData
{
    public CourseCatalogData(CatalogQuery query, IEnumerable<CatalogTerm> terms, IEnumerable<DepartmentOption> departments)
    {
        ArgumentNullException.ThrowIfNull(query);
        Query = query;
        Terms = SourceValidation.Snapshot(terms, SourceLimits.MaxTerms, nameof(terms));
        Departments = SourceValidation.Snapshot(departments, SourceLimits.MaxDepartments, nameof(departments));
        if (Terms.IsEmpty || Terms.Any(term => term.Year != query.Year)
            || Terms.DistinctBy(term => term.Selector).Count() != Terms.Length)
            throw new ArgumentException("A catalog needs unique source terms from the requested year.", nameof(terms));
        if (Departments.DistinctBy(department => (department.Level, department.Code)).Count() != Departments.Length)
            throw new ArgumentException("Department codes must be unique within each source level.", nameof(departments));
    }

    public CatalogQuery Query { get; }
    public ImmutableArray<CatalogTerm> Terms { get; }
    public ImmutableArray<DepartmentOption> Departments { get; }

    private readonly int? currentYear;
    private readonly string? currentTermSelector;

    /// <summary>
    /// The academic year of the term the source itself reports as current. It may differ from
    /// <see cref="Query"/>'s year (for example in January, while the winter term of the previous year is open).
    /// </summary>
    public int? CurrentYear
    {
        get => currentYear;
        init => currentYear = value is null ? null : SourceValidation.Year(value.Value);
    }

    /// <summary>The source-reported current term selector, belonging to <see cref="CurrentYear"/>.</summary>
    public string? CurrentTermSelector
    {
        get => currentTermSelector;
        init => currentTermSelector = value is null ? null : SourceValidation.Token(value, nameof(CurrentTermSelector));
    }

    /// <summary>The reported current term when it belongs to this catalog's year, otherwise null.</summary>
    public CatalogTerm? CurrentTerm => CurrentYear == Query.Year && CurrentTermSelector is { } selector
        ? Terms.FirstOrDefault(term => term.Selector == selector) : null;
}
