using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace SnuWatcher.Application.Sources;

/// <summary>
/// Source-boundary resource limits, not source pacing policy. Identifiers, filters, counts and page
/// limits match the existing SNU adapter. Collection and display caps are generous defensive bounds;
/// exceeding them must produce a typed failure rather than silently truncating verified source data.
/// String lengths count UTF-16 code units. Display permits source CR/LF/TAB; filters reject all controls.
/// </summary>
public static class SourceLimits
{
    public const int MaxIdentifier = 80;
    public const int MaxFilterText = 200;
    public const int MaxCount = 100_000;
    public const int MaxPages = 100;
    public const int MaxTerms = 128;
    public const int MaxDepartments = 10_000;
    public const int MaxRows = 10_000;
    public const int MaxMeetings = 128;
    public const int MaxProblems = 100;
    public const int MaxShortDisplay = 512;
    public const int MaxLongDisplay = 4096;
    public const int MaxRestrictions = 16_384;
}

internal static partial class SourceValidation
{
    internal static int Year(int year) => year is >= 2013 and <= 2200 ? year
        : throw new ArgumentOutOfRangeException(nameof(year), "The public source supports academic years 2013 through 2200.");

    internal static string Token(string value, string name) => value is { Length: > 0 and <= SourceLimits.MaxIdentifier }
        && TokenPattern().IsMatch(value) ? value
        : throw new ArgumentException("A bounded exact ASCII letter, digit or dot identifier is required.", name);

    internal static string? OptionalToken(string? value, string name)
    {
        RejectFilterControls(value, name);
        if (value?.Length > SourceLimits.MaxIdentifier)
            throw new ArgumentException("Source filter identifier exceeds its bound.", name);
        return string.IsNullOrWhiteSpace(value) ? null : Token(value, name);
    }

    internal static string? FilterText(string? value, string name)
    {
        RejectFilterControls(value, name);
        if (value?.Length > SourceLimits.MaxFilterText)
            throw new ArgumentException("Search text exceeds its bound.", name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static void RejectFilterControls(string? value, string name)
    {
        if (value?.Any(char.IsControl) == true)
            throw new ArgumentException("Search filters cannot contain control characters.", name);
    }

    internal static string Display(string value, int limit, string name, bool requireText = false)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (value.Length > limit || value.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))
            || (requireText && string.IsNullOrWhiteSpace(value)))
            throw new ArgumentException("Source display text is empty, unsafe or exceeds its bound.", name);
        return value;
    }

    internal static string? OptionalDisplay(string? value, int limit, string name) =>
        value is null ? null : Display(value, limit, name);

    internal static string Level(string value) => value is { Length: > 0 and <= SourceLimits.MaxIdentifier }
        && LevelPattern().IsMatch(value) ? value
        : throw new ArgumentException("A bounded exact department level identifier is required.", nameof(value));

    internal static int? Count(int? value, string name) => value is null or >= 0 and <= SourceLimits.MaxCount ? value
        : throw new ArgumentOutOfRangeException(name, "Source counts must be null or between zero and 100000.");

    internal static T Defined<T>(T value, string name) where T : struct, Enum => Enum.IsDefined(value) ? value
        : throw new ArgumentOutOfRangeException(name, "The source result value is unsupported.");

    internal static ImmutableArray<T> Snapshot<T>(IEnumerable<T> values, int limit, string name) where T : class
    {
        ArgumentNullException.ThrowIfNull(values, name);
        if (values is ImmutableArray<T> { IsDefault: true })
            throw new ArgumentException("The source collection must be initialized.", name);
        var snapshot = values.Take(limit + 1).ToImmutableArray();
        if (snapshot.Length > limit || snapshot.Any(item => item is null))
            throw new ArgumentException("The source collection exceeds its bound or contains null.", name);
        return snapshot;
    }

    internal static string Diagnostic(string code) => code is { Length: > 0 and <= 64 } && DiagnosticPattern().IsMatch(code)
        ? code : throw new ArgumentException("A bounded lowercase diagnostic code is required.", nameof(code));

    [GeneratedRegex(@"\A[A-Za-z0-9.]+\z")]
    private static partial Regex TokenPattern();
    [GeneratedRegex(@"\A[A-Za-z0-9_.]+\z")]
    private static partial Regex LevelPattern();
    [GeneratedRegex(@"\A[a-z0-9_.-]+\z")]
    private static partial Regex DiagnosticPattern();
}
