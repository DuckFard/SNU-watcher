using System.Globalization;
using System.Text.RegularExpressions;

namespace SnuWatcher.Application.Migration;

/// <summary>The old watcher's four-part identity. It does not infer the new full term or subsubject.</summary>
public sealed partial record LegacyCourseIdentity
{
    public const string FormatFamily = "python-watcher-v0";

    public LegacyCourseIdentity(int year, string semesterCode, string courseCode, string section)
    {
        if (year is < 2000 or > 2200) throw new ArgumentOutOfRangeException(nameof(year));
        if (semesterCode is null || !SemesterPattern().IsMatch(semesterCode))
            throw new ArgumentException("A legacy semester is U000200 followed by three ASCII digits.", nameof(semesterCode));
        if (string.IsNullOrEmpty(courseCode) || courseCode.Length > 80 || !CoursePattern().IsMatch(courseCode))
            throw new ArgumentException("A legacy course code needs bounded ASCII letters, digits or dots.", nameof(courseCode));
        if (section is null || !SectionPattern().IsMatch(section))
            throw new ArgumentException("A legacy section is exactly three ASCII digits.", nameof(section));
        Year = year;
        SemesterCode = semesterCode;
        CourseCode = courseCode;
        Section = section;
    }

    public int Year { get; }
    public string SemesterCode { get; }
    public string CourseCode { get; }
    public string Section { get; }
    public string StorageKey => string.Join(':', Year.ToString(CultureInfo.InvariantCulture), SemesterCode, CourseCode, Section);
    /// <summary>Stable source identity, independent of paths, labels, counts or timestamps.</summary>
    public string CanonicalKey => FormatFamily + ":" + StorageKey;

    [GeneratedRegex(@"\AU000200[0-9]{3}\z")]
    private static partial Regex SemesterPattern();
    [GeneratedRegex(@"\A[A-Za-z0-9.]+\z")]
    private static partial Regex CoursePattern();
    [GeneratedRegex(@"\A[0-9]{3}\z")]
    private static partial Regex SectionPattern();
}
