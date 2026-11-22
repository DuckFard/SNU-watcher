using System.Text.RegularExpressions;

namespace SnuWatcher.Core;

/// <summary>
/// The validated six-part identity of one enrollable section: academic year, semester,
/// detail semester, course code, three-digit section and subsubject (ADR 0002).
/// </summary>
/// <remarks>
/// Core never infers aliases between sub-terms. Infrastructure maps its source DTOs to this key.
/// Section numbers keep their leading zeros ("012" is not "12"). Course names are display data
/// and never part of the key.
/// </remarks>
public sealed partial record SectionKey
{
    public SectionKey(int year, string semesterCode, string detailSemesterCode,
        string courseCode, string section, string subsubjectCode)
    {
        if (year is < 2013 or > 2200)
            throw new ArgumentOutOfRangeException(nameof(year), "Academic year must be between 2013 and 2200.");
        Year = year;
        SemesterCode = Token(semesterCode, nameof(semesterCode));
        DetailSemesterCode = Token(detailSemesterCode, nameof(detailSemesterCode));
        CourseCode = Token(courseCode, nameof(courseCode));
        SubsubjectCode = Token(subsubjectCode, nameof(subsubjectCode));
        // \z, not $: "$" also matches before a trailing newline. [0-9], not \d: \d accepts other scripts' digits.
        if (section is null || !SectionPattern().IsMatch(section))
            throw new ArgumentException("A section is exactly three ASCII digits.", nameof(section));
        Section = section;
    }

    public int Year { get; }
    public string SemesterCode { get; }
    public string DetailSemesterCode { get; }
    public string CourseCode { get; }
    public string Section { get; }
    public string SubsubjectCode { get; }

    /// <summary>Short display form such as "F32.102(012)".</summary>
    public override string ToString() => $"{CourseCode}({Section})";

    private static string Token(string? value, string name) =>
        value is { Length: > 0 and <= 80 } && TokenPattern().IsMatch(value)
            ? value
            : throw new ArgumentException("A 1-80 character letter, digit or dot token is required.", name);

    [GeneratedRegex(@"\A[A-Za-z0-9.]+\z")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"\A[0-9]{3}\z")]
    private static partial Regex SectionPattern();
}
