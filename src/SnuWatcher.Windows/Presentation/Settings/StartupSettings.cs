using System.ComponentModel;
using System.Security;

namespace SnuWatcher.Windows.Presentation.Settings;

/// <summary>
/// The "start at sign-in" switch. It applies immediately and always shows Windows' actual state, because the user can
/// also change it in Windows' Startup apps settings; it is not one of the saved app preferences.
/// </summary>
public sealed class StartupSettings : INotifyPropertyChanged
{
    private readonly IStartupRegistration registration;
    private readonly Action openWindowsStartupSettings;
    private StartupRegistrationState state;
    private string? issueKey;

    /// <summary>Reads the current state from <paramref name="registration"/>; <paramref name="openWindowsStartupSettings"/>
    /// opens Windows' Startup apps page.</summary>
    public StartupSettings(IStartupRegistration registration, Action openWindowsStartupSettings)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(openWindowsStartupSettings);
        this.registration = registration;
        this.openWindowsStartupSettings = openWindowsStartupSettings;
        Refresh();
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Checked while Windows has an entry for this app, even if the user turned it off in Windows.</summary>
    public bool StartAtSignIn
    {
        get => state != StartupRegistrationState.Off;
        set { if (value != StartAtSignIn) Apply(value); }
    }

    /// <summary>The entry exists but Windows will not run it until the user turns it back on there.</summary>
    public bool IsDisabledInWindows => state == StartupRegistrationState.DisabledInWindows;

    /// <summary>A resource key describing the last failure, or null.</summary>
    public string? IssueKey => issueKey;

    /// <summary>Re-reads Windows' state, for example when Settings is opened again.</summary>
    public void Refresh()
    {
        var previous = (state, issueKey);
        try { state = registration.Read(); issueKey = null; }
        catch (Exception error) when (IsRegistryFailure(error)) { issueKey = "Ui.SignInStartUnavailable"; }
        if (previous != (state, issueKey)) Publish();
    }

    /// <summary>Opens Windows' Startup apps settings, where the user's own switch for the entry lives.</summary>
    public void OpenWindowsStartupSettings() => openWindowsStartupSettings();

    private void Apply(bool enabled)
    {
        try
        {
            if (enabled) registration.Enable(); else registration.Disable();
            issueKey = null;
        }
        catch (Exception error) when (IsRegistryFailure(error)) { issueKey = "Ui.SignInStartFailed"; }
        // Show what Windows actually has now, whether or not the change was accepted.
        try { state = registration.Read(); }
        catch (Exception error) when (IsRegistryFailure(error)) { issueKey ??= "Ui.SignInStartUnavailable"; }
        Publish();
    }

    private void Publish()
    {
        PropertyChanged?.Invoke(this, new(nameof(StartAtSignIn)));
        PropertyChanged?.Invoke(this, new(nameof(IsDisabledInWindows)));
        PropertyChanged?.Invoke(this, new(nameof(IssueKey)));
    }

    private static bool IsRegistryFailure(Exception error) =>
        error is UnauthorizedAccessException or SecurityException or IOException;
}
