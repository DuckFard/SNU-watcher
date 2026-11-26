using System.Globalization;
using Microsoft.Win32;

namespace SnuWatcher.Windows.Updates;

/// <summary>The update check's per-user switch and its last successful check.</summary>
public interface IUpdateCheckStore
{
    /// <summary>Whether to check; true until the user turns it off.</summary>
    bool ReadEnabled();
    /// <summary>Remembers the switch.</summary>
    void WriteEnabled(bool enabled);
    /// <summary>When the last check succeeded, or null.</summary>
    DateTimeOffset? ReadLastChecked();
    /// <summary>The newest version that check found, as text, or null.</summary>
    string? ReadNewestSeen();
    /// <summary>Records a successful check and the newest version it found, if any.</summary>
    void WriteChecked(DateTimeOffset checkedAtUtc, string? newestSeen);
}

/// <summary>
/// Keeps the update check's state under HKCU\Software\DuckFard\SnuWatcher, beside nothing else, so it needs no database
/// change. The installer removes the key on uninstall.
/// </summary>
public sealed class RegistryUpdateCheckStore : IUpdateCheckStore
{
    /// <summary>The key path; installer/SnuWatcher.iss removes the same key on uninstall.</summary>
    public const string KeyPath = @"Software\DuckFard\SnuWatcher";
    private const string EnabledName = "CheckForUpdates";
    private const string CheckedName = "UpdateCheckedUtc";
    private const string NewestName = "UpdateNewestSeen";
    private readonly RegistryKey root;
    private readonly string path;

    /// <summary>Uses the current user's <see cref="KeyPath"/>.</summary>
    public RegistryUpdateCheckStore() : this(Registry.CurrentUser, KeyPath) { }

    /// <summary>Test seam: the same values under another key.</summary>
    internal RegistryUpdateCheckStore(RegistryKey root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.root = root; this.path = path;
    }

    /// <inheritdoc/>
    public bool ReadEnabled()
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(EnabledName) is not int value || value != 0;
    }

    /// <inheritdoc/>
    public void WriteEnabled(bool enabled)
    {
        using var key = root.CreateSubKey(path, writable: true);
        key.SetValue(EnabledName, enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    /// <inheritdoc/>
    public DateTimeOffset? ReadLastChecked()
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(CheckedName) is string text && DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value) ? value : null;
    }

    /// <inheritdoc/>
    public string? ReadNewestSeen()
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(NewestName) as string;
    }

    /// <inheritdoc/>
    public void WriteChecked(DateTimeOffset checkedAtUtc, string? newestSeen)
    {
        using var key = root.CreateSubKey(path, writable: true);
        key.SetValue(CheckedName, checkedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), RegistryValueKind.String);
        if (newestSeen is null) key.DeleteValue(NewestName, throwOnMissingValue: false);
        else key.SetValue(NewestName, newestSeen, RegistryValueKind.String);
    }
}
