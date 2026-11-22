using System.Text.Json;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Windows.Hosting;

namespace SnuWatcher.Windows.Validation;

/// <summary>Closed installed qualification cases; OS permission changes remain a separate explicit human action.</summary>
public enum InstalledNativeDeliveryCase
{
    /// <summary>English fixture opening with system notification sound requested.</summary>
    EnglishSound,
    /// <summary>Korean fixture opening with explicit muted audio.</summary>
    KoreanMuted,
    /// <summary>Actual OS-disabled readiness must preserve a pending event without submission.</summary>
    PermissionDisabled,
}

/// <summary>Bounded opt-in intent, bound to one canonical run and installed executable fingerprint.</summary>
public sealed record InstalledNativeDeliveryRun
{
    internal InstalledNativeDeliveryRun(Guid runId, InstalledNativeDeliveryCase @case, string executableSha256, string pipelineSha256)
    { RunId = runId; Case = @case; ExecutableSha256 = executableSha256; PipelineSha256 = pipelineSha256; }
    /// <summary>Canonical fresh private run identity.</summary>
    public Guid RunId { get; }
    /// <summary>One closed native qualification case.</summary>
    public InstalledNativeDeliveryCase Case { get; }
    /// <summary>Expected exact installed executable SHA-256, uppercase ASCII hex.</summary>
    public string ExecutableSha256 { get; }
    /// <summary>Lowercase SHA-256 of the agreed fixed executable/library inventory, including every final LF.</summary>
    public string PipelineSha256 { get; }
    /// <summary>Stable external protocol case token.</summary>
    public string CaseName => Case switch { InstalledNativeDeliveryCase.EnglishSound => "en-sound", InstalledNativeDeliveryCase.KoreanMuted => "ko-muted", _ => "permission-disabled" };
    /// <summary>Language saved through the real preference store before the watch is created.</summary>
    public UiLanguage Language => Case == InstalledNativeDeliveryCase.KoreanMuted ? UiLanguage.Korean : UiLanguage.English;
    /// <summary>Only the Korean-muted case explicitly mutes the payload.</summary>
    public bool NotificationSoundMuted => Case == InstalledNativeDeliveryCase.KoreanMuted;
}

/// <summary>Pure strict argument/manifest recognition; it never creates a profile, registers or submits notifications.</summary>
public static class InstalledNativeDeliveryProtocol
{
    /// <summary>Recognized even when malformed so private requests cannot fall through to normal startup.</summary>
    public const string Prefix = "--native-delivery-validation";
    /// <summary>Closed manifest identity for this protocol version.</summary>
    public const string ManifestFormat = "snu-installed-native-delivery-v1";

    /// <summary>Accepts one exact canonical nonempty Guid-N argument and rejects mixed actions.</summary>
    public static ValidationArgumentDisposition ParseArguments(IReadOnlyList<string> arguments, out Guid runId)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        runId = Guid.Empty;
        if (!arguments.Any(argument => argument?.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) == true))
            return ValidationArgumentDisposition.NotRequested;
        if (arguments.Count != 1 || arguments[0] is not { } argument || !argument.StartsWith(Prefix + "=", StringComparison.Ordinal)
            || !Guid.TryParseExact(argument[(Prefix.Length + 1)..], "N", out var parsed) || parsed == Guid.Empty
            || parsed.ToString("N") != argument[(Prefix.Length + 1)..]) return ValidationArgumentDisposition.Invalid;
        runId = parsed;
        return ValidationArgumentDisposition.Valid;
    }

    /// <summary>Reads at most 1 KiB of exact fields, never a caller path or arbitrary action.</summary>
    public static InstalledNativeDeliveryRun ReadManifest(ReadOnlyMemory<byte> utf8, Guid expectedRunId)
    {
        if (expectedRunId == Guid.Empty || utf8.Length is < 1 or > 1024) throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid();
            var fields = root.EnumerateObject().ToArray();
            if (fields.Length != 5 || fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != 5
                || fields.Any(field => field.Name is not ("format" or "runId" or "case" or "executableSha256" or "pipelineSha256")
                    || field.Value.ValueKind != JsonValueKind.String)) throw Invalid();
            if (root.GetProperty("format").GetString() != ManifestFormat
                || root.GetProperty("runId").GetString() != expectedRunId.ToString("N")) throw Invalid();
            var hash = root.GetProperty("executableSha256").GetString()!;
            if (hash.Length != 64 || hash.Any(character => !char.IsAsciiDigit(character) && character is not (>= 'A' and <= 'F'))) throw Invalid();
            var pipelineHash = root.GetProperty("pipelineSha256").GetString()!;
            if (pipelineHash.Length != 64 || pipelineHash.Any(character => !char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))) throw Invalid();
            var @case = root.GetProperty("case").GetString() switch
            {
                "en-sound" => InstalledNativeDeliveryCase.EnglishSound,
                "ko-muted" => InstalledNativeDeliveryCase.KoreanMuted,
                "permission-disabled" => InstalledNativeDeliveryCase.PermissionDisabled,
                _ => throw Invalid(),
            };
            return new(expectedRunId, @case, hash, pipelineHash);
        }
        catch (JsonException) { throw Invalid(); }
    }
    private static InvalidDataException Invalid() => new("The installed native-delivery validation manifest is invalid.");
}
