using System.Text.Json;

namespace SnuWatcher.Windows.Updates;

/// <summary>
/// Reads this project's release list from the GitHub API. Only the tag names of published (non-draft) releases are
/// used, and only tags that are recognized versions; the release page is then built from the version, so nothing
/// else in the response, such as a link, is ever followed.
/// </summary>
public static class GitHubReleaseFeed
{
    /// <summary>The only release list the app asks for.</summary>
    public static readonly Uri ReleasesApi = new("https://api.github.com/repos/DuckFard/SNU-watcher/releases?per_page=20");

    /// <summary>Larger responses are rejected unread; twenty releases with full notes are far smaller.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>Returns the newest published version in a release list, or null if it has none.</summary>
    /// <exception cref="InvalidDataException">The response is too large or is not a release list.</exception>
    /// <exception cref="JsonException">The response is not valid JSON.</exception>
    public static ReleaseVersion? Newest(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxBytes) throw new InvalidDataException("The release list is too large.");
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 16 });
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("The response is not a release list.");
        ReleaseVersion? newest = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object) continue;
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind != JsonValueKind.False) continue;
            if (!release.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String) continue;
            if (ReleaseVersion.TryParse(tag.GetString(), out var version) && (newest is null || version! > newest)) newest = version;
        }
        return newest;
    }

    /// <summary>The release page for <paramref name="version"/> in this repository.</summary>
    public static Uri ReleasePage(ReleaseVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new("https://github.com/DuckFard/SNU-watcher/releases/tag/" + version.Tag);
    }
}
