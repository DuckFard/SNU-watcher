namespace SnuWatcher.Windows;

/// <summary>Protects app-owned shortcut registration from modifying another executable's link.</summary>
public static class ShortcutBindingPolicy
{
    /// <summary>Accepts only a matching absolute executable target and a nonempty registered CLSID.</summary>
    public static Guid Validate(string shortcutTarget, string processPath, string? registeredActivator)
    {
        if (!Path.IsPathFullyQualified(shortcutTarget) || !Path.IsPathFullyQualified(processPath) ||
            !string.Equals(Path.GetFullPath(shortcutTarget), Path.GetFullPath(processPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Shortcut must belong to this installed application.");
        if (!Guid.TryParse(registeredActivator, out var activator) || activator == Guid.Empty)
            throw new ArgumentException("The app's registered notification activator is unavailable.");
        return activator;
    }
}
