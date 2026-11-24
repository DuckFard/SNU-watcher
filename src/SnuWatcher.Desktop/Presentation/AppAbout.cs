using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnuWatcher.Desktop.Presentation;

/// <summary>Fixed facts and destinations for the About card. Nothing here comes from the network or student input.</summary>
public static class AppAbout
{
    private static readonly Assembly App = typeof(AppAbout).Assembly;

    /// <summary>The release version, for example 0.1.0-preview.2.</summary>
    public static string Version { get; } = AboutVersion.Format(
        App.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, App.GetName().Version);

    /// <summary>The application icon's largest frame, or null if it cannot be decoded.</summary>
    public static ImageSource? Icon { get; } = LoadIcon();

    public static Uri ProjectPage { get; } = new("https://github.com/DuckFard/SNU-watcher");

    public static Uri ReleaseNotes { get; } = new("https://github.com/DuckFard/SNU-watcher/releases");

    /// <summary>Third-party license notices written beside the exe by the release build; absent in development builds.</summary>
    public static string NoticesPath { get; } = Path.Combine(AppContext.BaseDirectory, "DISTRIBUTION-LICENSES.txt");

    public static bool HasNotices { get; } = File.Exists(NoticesPath);

    /// <summary>Opens one of the fixed destinations above with its default handler.</summary>
    internal static bool TryOpen(string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }
    }

    private static ImageSource? LoadIcon()
    {
        try
        {
            // An .ico shown directly would use its first (16 px) frame; take the largest and let the Image scale it down.
            var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/SnuWatcher.Desktop;component/Assets/SnuWatcher.ico"),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderByDescending(candidate => candidate.PixelWidth).First();
            frame.Freeze();
            return frame;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or InvalidOperationException or UriFormatException)
        {
            return null;
        }
    }
}
