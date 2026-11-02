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
}
