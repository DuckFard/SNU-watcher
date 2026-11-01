using System.Text.Json;
using SnuWatcher.Application.Presentation.Diagnostics;

namespace SnuWatcher.Windows.Diagnostics;

/// <summary>Explicit export schema, not reflection serialization of models. Never add arbitrary context or exception strings.</summary>
public static class DiagnosticsJson
{
    /// <summary>Maximum complete UTF-8 report size.</summary>
    public const int MaximumBytes = 64 * 1024;
    /// <summary>Serializes only the closed diagnostic schema; no reflection over arbitrary models is used.</summary>
    public static byte[] Serialize(DiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject(); json.WriteString("format", "snu-watcher-diagnostics-v1");
            json.WriteString("capturedAtUtc", snapshot.CapturedAtUtc);
            json.WriteString("applicationVersion", snapshot.ApplicationVersion.ToString()); json.WriteString("runtimeVersion", snapshot.RuntimeVersion.ToString());
            var state = snapshot.Runtime;
            json.WriteStartObject("runtime"); json.WriteString("state", state.State.ToString());
            json.WriteString("source", state.Source.ToString()); json.WriteString("notifications", state.Notifications.ToString());
            json.WriteString("appliedProfile", state.AppliedProfile?.ToString());
            Boolean(json, "online", state.IsOnline); Boolean(json, "suspended", state.IsSuspended);
            json.WriteStartObject("savedPreferences"); json.WriteString("language", state.Preferences.Language?.ToString());
            json.WriteString("desiredProfile", state.Preferences.DesiredProfile?.ToString());
            json.WriteString("closeBehavior", state.Preferences.CloseBehavior?.ToString()); Boolean(json, "soundMuted", state.Preferences.SoundMuted);
            json.WriteEndObject(); json.WriteEndObject();
            json.WriteStartObject("storage"); File(json, "database", snapshot.Storage.Database); File(json, "wal", snapshot.Storage.Wal); File(json, "shm", snapshot.Storage.Shm);
            Number(json, "totalBytes", snapshot.Storage.TotalBytes); json.WriteNumber("warningThresholdBytes", StorageFootprint.WarningThresholdBytes);
            json.WriteBoolean("sizeWarning", snapshot.Storage.HasSizeWarning); json.WriteEndObject(); json.WriteEndObject();
        }
        if (buffer.Length > MaximumBytes) throw new InvalidOperationException("Diagnostics exceeded the export bound.");
        return buffer.ToArray();
    }
    private static void File(Utf8JsonWriter writer, string name, DiagnosticFileSize file)
    { writer.WriteStartObject(name); writer.WriteString("state", file.State.ToString()); Number(writer, "bytes", file.Bytes); writer.WriteEndObject(); }
    private static void Number(Utf8JsonWriter writer, string name, long? value)
    { if (value is { } number) writer.WriteNumber(name, number); else writer.WriteNull(name); }
    private static void Boolean(Utf8JsonWriter writer, string name, bool? value)
    { if (value is { } boolean) writer.WriteBoolean(name, boolean); else writer.WriteNull(name); }
}

/// <summary>Creates a new user-selected report only. Once writing begins, the actual bounded write/flush is retained despite late cancellation.</summary>
public static class DiagnosticsFileExporter
{
    /// <summary>Writes and flushes one create-new report off the UI thread; existing destinations are never replaced.</summary>
    public static Task<DiagnosticsExportOutcome> SaveAsync(string path, DiagnosticsSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || string.IsNullOrEmpty(Path.GetFileName(path))) throw new ArgumentException("Select an absolute file destination.", nameof(path));
        var bytes = DiagnosticsJson.Serialize(snapshot);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            file.Write(bytes); file.Flush(flushToDisk: true);
            return DiagnosticsExportOutcome.Saved;
        }, cancellationToken);
    }
}
