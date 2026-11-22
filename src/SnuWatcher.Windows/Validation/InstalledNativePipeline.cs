using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SnuWatcher.Windows.Validation;

/// <summary>Immutable installed fingerprints; executable identity alone cannot identify the managed pipeline.</summary>
/// <param name="ExecutableSha256">Uppercase raw apphost SHA-256.</param>
/// <param name="PipelineSha256">Lowercase fixed-six inventory SHA-256.</param>
public sealed record InstalledNativePipelineFingerprint(string ExecutableSha256, string PipelineSha256);

/// <summary>Read-only fixed-file preflight. This helper never launches, initializes native APIs, or accepts caller file lists.</summary>
public static class InstalledNativePipeline
{
    /// <summary>Fixed protocol order shared with the trusted release inventory and prelaunch script.</summary>
    public static ImmutableArray<string> FileNames { get; } = ["SnuWatcher.Desktop.exe", "SnuWatcher.Desktop.dll",
        "SnuWatcher.Windows.dll", "SnuWatcher.Infrastructure.dll", "SnuWatcher.Application.dll", "SnuWatcher.Core.dll"];

    /// <summary>Hashes six existing ordinary files; a changed managed library changes the aggregate even if the apphost is identical.</summary>
    public static InstalledNativePipelineFingerprint ReadInstalled(string installedDirectory)
    {
        var files = ReadFiles(installedDirectory);
        return Fingerprint(files);
    }

    /// <summary>Verifies an independently pinned raw Build-Native manifest before accepting its expected payload facts.</summary>
    public static InstalledNativePipelineFingerprint VerifyTrustedBuild(string installedDirectory,
        ReadOnlyMemory<byte> manifest, string expectedManifestSha256)
    {
        if (manifest.Length is < 1 or > 2_097_152 || !IsLowerHash(expectedManifestSha256)
            || Digest(manifest.Span) != expectedManifestSha256) throw Invalid();
        var actual = ReadFiles(installedDirectory);
        try
        {
            using var document = JsonDocument.Parse(manifest, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(p => p.Name).Distinct().Count() != root.EnumerateObject().Count()
                || root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("source").GetProperty("revision").GetString() is not { Length: 40 } revision
                || revision.Any(c => !char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'))
                || root.GetProperty("source").GetProperty("dirty").ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || root.GetProperty("version").ValueKind != JsonValueKind.String
                || root.GetProperty("installerVersion").ValueKind != JsonValueKind.String) throw Invalid();
            var payload = root.GetProperty("payload");
            if (payload.ValueKind != JsonValueKind.Array || payload.GetArrayLength() is < 6 or > 2000) throw Invalid();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in payload.EnumerateArray())
            {
                var path = entry.GetProperty("path").GetString();
                if (path is null || !FileNames.Contains(path)) continue;
                if (!seen.Add(path) || entry.GetProperty("bytes").GetInt64() != actual[path].Bytes
                    || entry.GetProperty("sha256").GetString() != actual[path].Hash) throw Invalid();
            }
            if (seen.Count != FileNames.Length) throw Invalid();
            return Fingerprint(actual);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw Invalid(); }
    }

    private static Dictionary<string, (long Bytes, string Hash)> ReadFiles(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = new DirectoryInfo(Path.GetFullPath(directory));
        for (var current = root; current is not null; current = current.Parent)
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0) throw Invalid();
        var result = new Dictionary<string, (long, string)>(StringComparer.Ordinal);
        foreach (var name in FileNames)
        {
            var path = Path.Combine(root.FullName, name);
            if (!File.Exists(path) || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw Invalid();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 or > 67_108_864) throw Invalid();
            result.Add(name, (stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream))));
        }
        return result;
    }

    private static InstalledNativePipelineFingerprint Fingerprint(Dictionary<string, (long Bytes, string Hash)> files) =>
        new(files[FileNames[0]].Hash.ToUpperInvariant(), Digest(Encoding.UTF8.GetBytes(
            string.Concat(FileNames.Select(name => name + "|" + files[name].Hash + "\n")))));
    private static bool IsLowerHash(string? value) => value is { Length: 64 }
        && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static InvalidDataException Invalid() => new("The installed payload does not match the pinned build inventory.");
}
