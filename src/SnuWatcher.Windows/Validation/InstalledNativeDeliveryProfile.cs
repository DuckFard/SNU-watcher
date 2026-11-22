using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using SnuWatcher.Windows.Hosting;
using SnuWatcher.Windows.Validation;

namespace SnuWatcher.Windows.Validation;

/// <summary>A one-time pre-registration claim of an otherwise empty, marked private validation profile.</summary>
public sealed class InstalledNativeDeliveryProfile
{
    /// <summary>Closed native intent leaf inside the existing derived validation profile.</summary>
    public const string IntentLeaf = "native-delivery-validation.json";
    /// <summary>Create-new one-time claim; never removed or overwritten by the application.</summary>
    public const string ClaimLeaf = "native-delivery-claim.json";
    private InstalledNativeDeliveryProfile(RuntimeProfile profile, InstalledNativeDeliveryRun intent)
    { Profile = profile; Intent = intent; }
    /// <summary>Existing marked private profile, never the production profile.</summary>
    public RuntimeProfile Profile { get; }
    /// <summary>Strict case/run/executable intent read before native registration.</summary>
    public InstalledNativeDeliveryRun Intent { get; }

    /// <summary>Claims only a fresh two-marker profile and matches the independently computed executable fingerprint.</summary>
    public static InstalledNativeDeliveryProfile Claim(Guid runId, string actualExecutableSha256, string actualPipelineSha256)
    {
        var profile = RuntimeProfile.OpenValidationRun(runId);
        var entries = Directory.EnumerateFileSystemEntries(profile.DirectoryPath).Take(3).ToArray();
        if (entries.Length != 2 || entries.Any(path => Path.GetFileName(path) is not ("runtime-validation.json" or IntentLeaf))) throw Invalid();
        var intentPath = Path.Combine(profile.DirectoryPath, IntentLeaf);
        RequireOrdinaryLeaf(intentPath);
        byte[] bytes;
        using (var stream = new FileStream(intentPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length is < 1 or > 1024) throw Invalid();
            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw Invalid();
        }
        var intent = InstalledNativeDeliveryProtocol.ReadManifest(bytes, runId);
        if (!string.Equals(intent.ExecutableSha256, actualExecutableSha256, StringComparison.Ordinal)
            || !string.Equals(intent.PipelineSha256, actualPipelineSha256, StringComparison.Ordinal)) throw Invalid();
        profile.ValidateExisting();
        try
        {
            using var claim = new FileStream(Path.Combine(profile.DirectoryPath, ClaimLeaf), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            JsonSerializer.Serialize(claim, new { format = "snu-installed-native-delivery-claim-v1", runId = runId.ToString("N"),
                @case = intent.CaseName, executableSha256 = actualExecutableSha256, pipelineSha256 = actualPipelineSha256,
                processId = Environment.ProcessId });
            claim.Flush(flushToDisk: true);
        }
        catch (IOException) { throw Invalid(); }
        return new(profile, intent);
    }
    private static void RequireOrdinaryLeaf(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw Invalid();
    }
    private static InvalidDataException Invalid() => new("The installed native validation run is not fresh and correctly marked.");
}
