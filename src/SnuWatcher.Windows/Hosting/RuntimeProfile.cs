using System.Globalization;
using System.Text.Json;

namespace SnuWatcher.Windows.Hosting;

/// <summary>Early validation routing; invalid validation prefixes never fall through into native startup.</summary>
public enum ValidationArgumentDisposition
{
    /// <summary>The existing M2/native route is unchanged.</summary>
    NotRequested,
    /// <summary>One canonical nonempty run GUID was provided.</summary>
    Valid,
    /// <summary>A validation prefix was present but malformed or mixed with another action.</summary>
    Invalid,
}

/// <summary>Closed profile derivation. Validation accepts a marked run ID, never a caller-selected database path.</summary>
public sealed class RuntimeProfile
{
    private const string ValidationPrefix = "--monitor-validation";
    private const string ManifestFormat = "snu-runtime-validation-v1";
    private RuntimeProfile(Guid? runId)
    {
        RunId = runId;
        DirectoryPath = runId is { } id ? Path.Combine(ValidationRoot, id.ToString("N"))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DuckFard", "SnuWatcher");
        DirectoryPath = Path.GetFullPath(DirectoryPath);
        DatabasePath = Path.Combine(DirectoryPath, "watches.sqlite3");
        ManifestPath = Path.Combine(DirectoryPath, "runtime-validation.json");
        ResultPath = Path.Combine(DirectoryPath, "result-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".json");
        ApplicationId = runId is { } run ? "DuckFard.SnuWatcher.Validation." + run.ToString("N") : "DuckFard.SnuWatcher";
    }
    /// <summary>Private validation identity; absent for the fixed normal profile.</summary>
    public Guid? RunId { get; }
    /// <summary>Whether this is a marked isolated validation run.</summary>
    public bool IsValidation => RunId is not null;
    /// <summary>Canonical derived profile directory.</summary>
    public string DirectoryPath { get; }
    /// <summary>Fixed database leaf, never caller supplied.</summary>
    public string DatabasePath { get; }
    /// <summary>Bounded validation marker path.</summary>
    public string ManifestPath { get; }
    /// <summary>Process-specific private result path.</summary>
    public string ResultPath { get; }
    /// <summary>Stable normal ID or separate run-specific ownership ID.</summary>
    public string ApplicationId { get; }
    private static string ValidationRoot => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SnuWatcher-runtime-validation"));
    internal static RuntimeProfile Production() => new(null); // Pure path derivation; no I/O before the owner is acquired.

    /// <summary>Creates a new marked private run without overwriting an existing run.</summary>
    public static RuntimeProfile CreateValidationRun(Guid runId)
    {
        ValidateRunId(runId);
        var result = new RuntimeProfile(runId);
        RequireOrdinaryChain(result.DirectoryPath, allowMissing: true);
        if (Path.Exists(result.DirectoryPath)) throw InvalidProfile();
        Directory.CreateDirectory(result.DirectoryPath);
        RequireOrdinaryChain(result.DirectoryPath, allowMissing: false);
        // CreateNew never overwrites a contender's marker. A failed creation leaves an invalid,
        // isolated run rather than deleting a directory another process may now own.
        using var marker = new FileStream(result.ManifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(marker, new { format = ManifestFormat, runId = runId.ToString("N") });
        marker.Flush(flushToDisk: true);
        return result;
    }

    /// <summary>Reads a previously marked run, including a second contender's ownership probe.</summary>
    public static RuntimeProfile OpenValidationRun(Guid runId)
    {
        ValidateRunId(runId);
        var result = new RuntimeProfile(runId);
        result.ValidateExisting();
        return result;
    }

    /// <summary>Recognizes even malformed validation prefixes before any native initialization.</summary>
    public static ValidationArgumentDisposition ParseValidationArguments(IReadOnlyList<string> args, out Guid runId)
    {
        ArgumentNullException.ThrowIfNull(args);
        runId = Guid.Empty;
        if (!args.Any(arg => arg?.StartsWith(ValidationPrefix, StringComparison.OrdinalIgnoreCase) == true))
            return ValidationArgumentDisposition.NotRequested;
        if (args.Count != 1 || args[0] is not { } arg || !arg.StartsWith(ValidationPrefix + "=", StringComparison.Ordinal)
            || !Guid.TryParseExact(arg[(ValidationPrefix.Length + 1)..], "N", out var parsed)
            || parsed == Guid.Empty || parsed.ToString("N") != arg[(ValidationPrefix.Length + 1)..])
            return ValidationArgumentDisposition.Invalid;
        runId = parsed;
        return ValidationArgumentDisposition.Valid;
    }

    internal void EnsureDirectoryAfterOwnership()
    {
        if (IsValidation) { ValidateExisting(); return; }
        RequireOrdinaryChain(DirectoryPath, allowMissing: true);
        Directory.CreateDirectory(DirectoryPath);
        RequireOrdinaryChain(DirectoryPath, allowMissing: false);
        RequireDatabaseLeaves();
    }

    /// <summary>Revalidate immediately before a private write; paths cannot be supplied or changed by the caller.</summary>
    public void ValidateExisting()
    {
        if (RunId is not { } run) throw InvalidProfile();
        RequireOrdinaryChain(DirectoryPath, allowMissing: false);
        RequireOrdinaryLeaf(ManifestPath, allowMissing: false);
        RequireDatabaseLeaves();
        RequireOrdinaryLeaf(ResultPath, allowMissing: true);
        try
        {
            using var stream = new FileStream(ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 or > 1024) throw InvalidProfile();
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw InvalidProfile();
            var fields = root.EnumerateObject().ToArray();
            if (fields.Length != 2 || fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != 2
                || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != ManifestFormat
                || !root.TryGetProperty("runId", out var identity) || identity.ValueKind != JsonValueKind.String || identity.GetString() != run.ToString("N"))
                throw InvalidProfile();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { throw InvalidProfile(); }
    }

    private static void ValidateRunId(Guid id) { if (id == Guid.Empty) throw new ArgumentException("A nonempty validation run ID is required.", nameof(id)); }
    private static InvalidOperationException InvalidProfile() => new("The private runtime validation profile is invalid.");

    private static void RequireOrdinaryChain(string path, bool allowMissing)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw InvalidProfile();
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0) throw InvalidProfile();
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            { if (!allowMissing) throw InvalidProfile(); }
        }
    }

    private static void RequireOrdinaryLeaf(string path, bool allowMissing)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw InvalidProfile();
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { if (!allowMissing) throw InvalidProfile(); }
    }

    private void RequireDatabaseLeaves()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            RequireOrdinaryLeaf(DatabasePath + suffix, allowMissing: true);
    }
}
