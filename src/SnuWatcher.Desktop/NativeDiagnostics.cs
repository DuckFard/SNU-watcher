using System;
using System.IO;
using System.Text.Json;

namespace SnuWatcher.Desktop;

/// <summary>Small local feasibility report, containing no student data or source credentials.</summary>
internal static class NativeDiagnostics
{
    public static string ReportPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuckFard", "SnuWatcher", "native-diagnostics.json");

    public static void Write(string stage, string status, bool owner, uint? notificationId = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
        var report = new
        {
            observedAt = DateTimeOffset.UtcNow,
            stage,
            status,
            configuredAppIdentity = NativeAppIdentity.AppId,
            instanceOwner = owner,
            notificationId,
            visibleBannerConfirmed = false,
            cleanVmGatePassed = false,
            build = "M2 feasibility candidate"
        };
        File.WriteAllText(ReportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
