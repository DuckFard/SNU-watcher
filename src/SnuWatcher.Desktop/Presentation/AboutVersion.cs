using System;

namespace SnuWatcher.Desktop.Presentation;

/// <summary>The version a student sees: the release version (for example 0.1.0-preview.2) without build metadata.</summary>
internal static class AboutVersion
{
    internal static string Format(string? informationalVersion, Version? assemblyVersion)
    {
        // Release builds stamp "<release.json version>+<commit>"; the commit is not useful to a student.
        var release = informationalVersion?.Split('+', 2)[0].Trim();
        if (!string.IsNullOrEmpty(release)) return release;
        return assemblyVersion is null ? "—" : assemblyVersion.ToString(3);
    }
}
