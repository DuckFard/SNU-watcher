using System.Globalization;
using System.Text.RegularExpressions;

namespace SnuWatcher.Windows.Updates;

/// <summary>
/// A published SNU Watcher version: MAJOR.MINOR.PATCH, optionally followed by "-preview.N", as in the release tags
/// (v0.1.0-preview.3). A release sorts after every preview of the same numbers, and previews sort by N. Anything else
/// is not a version this app recognizes, so an unexpected tag can never become an update offer.
/// </summary>
public sealed partial record ReleaseVersion : IComparable<ReleaseVersion>
{
    private ReleaseVersion(int major, int minor, int patch, int? preview)
    { Major = major; Minor = minor; Patch = patch; Preview = preview; }

    /// <summary>The first version number.</summary>
    public int Major { get; }
    /// <summary>The second version number.</summary>
    public int Minor { get; }
    /// <summary>The third version number.</summary>
    public int Patch { get; }
    /// <summary>The N of "-preview.N", or null for a release.</summary>
    public int? Preview { get; }

    /// <summary>The release tag for this version, for example "v0.1.0-preview.3".</summary>
    public string Tag => "v" + ToString();

    /// <summary>Accepts a version or tag, with or without the leading "v"; no build metadata or other suffixes.</summary>
    public static bool TryParse(string? text, out ReleaseVersion? version)
    {
        version = null;
        if (text is null) return false;
        var match = Pattern().Match(text);
        if (!match.Success) return false;
        static int Number(Group group) => int.Parse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture);
        version = new(Number(match.Groups["major"]), Number(match.Groups["minor"]), Number(match.Groups["patch"]),
            match.Groups["preview"].Success ? Number(match.Groups["preview"]) : null);
        return true;
    }

    /// <inheritdoc/>
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0) return core;
        if (Preview == other.Preview) return 0;
        // A release (no preview number) is newer than any preview of the same numbers.
        if (Preview is null) return 1;
        if (other.Preview is null) return -1;
        return Preview.Value.CompareTo(other.Preview.Value);
    }

    /// <summary>Whether <paramref name="left"/> is older than <paramref name="right"/>.</summary>
    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;
    /// <summary>Whether <paramref name="left"/> is newer than <paramref name="right"/>.</summary>
    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;
    /// <summary>Whether <paramref name="left"/> is older than or the same as <paramref name="right"/>.</summary>
    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;
    /// <summary>Whether <paramref name="left"/> is newer than or the same as <paramref name="right"/>.</summary>
    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    /// <summary>The version without the leading "v", for example "0.1.0-preview.3".</summary>
    public override string ToString() => Preview is { } preview
        ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-preview.{preview}")
        : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    // Canonical numbers only (no leading zeros), at most five digits each, and nothing before or after.
    [GeneratedRegex(@"\Av?(?<major>0|[1-9][0-9]{0,4})\.(?<minor>0|[1-9][0-9]{0,4})\.(?<patch>0|[1-9][0-9]{0,4})(?:-preview\.(?<preview>0|[1-9][0-9]{0,4}))?\z",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Pattern();
}
