using Microsoft.Win32;

namespace SnuWatcher.Windows;

/// <summary>Whether Windows starts the app when the current user signs in.</summary>
public enum StartupRegistrationState
{
    /// <summary>No sign-in entry for this executable.</summary>
    Off,
    /// <summary>An entry for this executable that Windows will run at sign-in.</summary>
    On,
    /// <summary>An entry exists, but the user turned it off in Windows' Startup apps settings or Task Manager.</summary>
    DisabledInWindows,
}

/// <summary>Reads and changes the current user's sign-in start for this app.</summary>
public interface IStartupRegistration
{
    /// <summary>Reads whether Windows will start this app at sign-in.</summary>
    StartupRegistrationState Read();
    /// <summary>Adds this app's sign-in entry; the user's own switch in Windows is left as it is.</summary>
    void Enable();
    /// <summary>Removes this app's sign-in entry, if present.</summary>
    void Disable();
}

/// <summary>
/// Per-user sign-in start through the classic <c>Run</c> key, which Windows' Startup apps settings and Task Manager
/// list and can switch off. That switch (<c>StartupApproved</c>) belongs to the user: this class reports it but never
/// changes it. The installer removes the same value on uninstall.
/// </summary>
public sealed class RunKeyStartupRegistration : IStartupRegistration
{
    /// <summary>The registry value name; installer/SnuWatcher.iss removes the same name on uninstall.</summary>
    public const string ValueName = "DuckFard.SnuWatcher";
    /// <summary>Starts in the tray without opening the window.</summary>
    public const string BackgroundArgument = "--background";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private readonly RegistryKey root;
    private readonly string runKeyPath;
    private readonly string approvedKeyPath;

    /// <summary>Registers <paramref name="executablePath"/> under the current user's Run key.</summary>
    public RunKeyStartupRegistration(string executablePath)
        : this(Registry.CurrentUser, RunKeyPath, ApprovedKeyPath, executablePath) { }

    /// <summary>Test seam: the same behaviour under other keys.</summary>
    internal RunKeyStartupRegistration(RegistryKey root, string runKeyPath, string approvedKeyPath, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(runKeyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedKeyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath) || executablePath.Contains('"'))
            throw new ArgumentException("The executable path must be absolute.", nameof(executablePath));
        this.root = root; this.runKeyPath = runKeyPath; this.approvedKeyPath = approvedKeyPath;
        Command = "\"" + Path.GetFullPath(executablePath) + "\" " + BackgroundArgument;
    }

    /// <summary>The exact command written for this executable.</summary>
    public string Command { get; }

    /// <inheritdoc/>
    public StartupRegistrationState Read()
    {
        using (var run = root.OpenSubKey(runKeyPath))
        {
            // An entry for another copy of the app (an older install location) is not this app's start.
            if (run?.GetValue(ValueName) is not string command || !string.Equals(command, Command, StringComparison.OrdinalIgnoreCase))
                return StartupRegistrationState.Off;
        }
        using var approved = root.OpenSubKey(approvedKeyPath);
        // Windows records the user's switch as binary data whose first byte is odd when the entry is turned off.
        return approved?.GetValue(ValueName) is byte[] { Length: > 0 } state && (state[0] & 1) == 1
            ? StartupRegistrationState.DisabledInWindows
            : StartupRegistrationState.On;
    }

    /// <inheritdoc/>
    public void Enable()
    {
        using var run = root.CreateSubKey(runKeyPath, writable: true);
        run.SetValue(ValueName, Command, RegistryValueKind.String);
    }

    /// <inheritdoc/>
    public void Disable()
    {
        using var run = root.OpenSubKey(runKeyPath, writable: true);
        run?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
