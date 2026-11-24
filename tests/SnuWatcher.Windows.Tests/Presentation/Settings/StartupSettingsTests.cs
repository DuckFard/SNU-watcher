using System.ComponentModel;
using System.Security;
using SnuWatcher.Windows.Presentation.Settings;
using Xunit;

namespace SnuWatcher.Windows.Tests.Presentation.Settings;

public sealed class StartupSettingsTests
{
    internal sealed class FakeRegistration : IStartupRegistration
    {
        public StartupRegistrationState State { get; set; }
        public Exception? ReadFailure { get; set; }
        public Exception? WriteFailure { get; set; }
        public int Reads { get; private set; }
        public int Enables { get; private set; }
        public int Disables { get; private set; }

        public StartupRegistrationState Read() { Reads++; if (ReadFailure is { } failure) throw failure; return State; }
        public void Enable() { Enables++; if (WriteFailure is { } failure) throw failure; if (State == StartupRegistrationState.Off) State = StartupRegistrationState.On; }
        public void Disable() { Disables++; if (WriteFailure is { } failure) throw failure; State = StartupRegistrationState.Off; }
    }

    private static List<string?> Observe(StartupSettings settings)
    {
        var changes = new List<string?>();
        settings.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        return changes;
    }

    [Fact]
    public void ReflectsWindowsStateAndAppliesChangesImmediately()
    {
        var registration = new FakeRegistration();
        var settings = new StartupSettings(registration, () => { });
        Assert.False(settings.StartAtSignIn);
        Assert.False(settings.IsDisabledInWindows);
        Assert.Null(settings.IssueKey);
        var changes = Observe(settings);

        settings.StartAtSignIn = true;
        Assert.Equal(1, registration.Enables);
        Assert.True(settings.StartAtSignIn);
        Assert.Contains(nameof(StartupSettings.StartAtSignIn), changes);

        settings.StartAtSignIn = true;
        Assert.Equal(1, registration.Enables);

        settings.StartAtSignIn = false;
        Assert.Equal(1, registration.Disables);
        Assert.False(settings.StartAtSignIn);
    }

    [Fact]
    public void AnEntryTurnedOffInWindowsStaysCheckedAndExplainsWhereToTurnItOn()
    {
        var opened = 0;
        var registration = new FakeRegistration { State = StartupRegistrationState.DisabledInWindows };
        var settings = new StartupSettings(registration, () => opened++);
        Assert.True(settings.StartAtSignIn);
        Assert.True(settings.IsDisabledInWindows);

        settings.OpenWindowsStartupSettings();
        Assert.Equal(1, opened);

        // Unchecking removes the app's entry; Windows' own switch is no longer relevant.
        settings.StartAtSignIn = false;
        Assert.False(settings.StartAtSignIn);
        Assert.False(settings.IsDisabledInWindows);
    }

    public static TheoryData<Exception> RegistryFailures => new()
        { new UnauthorizedAccessException(), new SecurityException(), new IOException() };

    [Theory]
    [MemberData(nameof(RegistryFailures))]
    public void ARejectedChangeShowsWhatWindowsActuallyHas(Exception failure)
    {
        var registration = new FakeRegistration();
        var settings = new StartupSettings(registration, () => { });
        registration.WriteFailure = failure;

        settings.StartAtSignIn = true;
        Assert.False(settings.StartAtSignIn);
        Assert.Equal("Ui.SignInStartFailed", settings.IssueKey);

        registration.WriteFailure = null;
        settings.StartAtSignIn = true;
        Assert.True(settings.StartAtSignIn);
        Assert.Null(settings.IssueKey);
    }

    [Theory]
    [MemberData(nameof(RegistryFailures))]
    public void AnUnreadableSettingIsReportedAndRefreshRecovers(Exception failure)
    {
        var registration = new FakeRegistration { ReadFailure = failure };
        var settings = new StartupSettings(registration, () => { });
        Assert.Equal("Ui.SignInStartUnavailable", settings.IssueKey);
        Assert.False(settings.StartAtSignIn);

        settings.StartAtSignIn = true;
        Assert.Equal("Ui.SignInStartUnavailable", settings.IssueKey);

        registration.ReadFailure = null;
        settings.Refresh();
        Assert.Null(settings.IssueKey);
        Assert.True(settings.StartAtSignIn);
    }

    [Fact]
    public void RefreshPublishesOnlyWhenWindowsStateChanged()
    {
        var registration = new FakeRegistration();
        var settings = new StartupSettings(registration, () => { });
        var changes = Observe(settings);

        settings.Refresh();
        Assert.Empty(changes);

        registration.State = StartupRegistrationState.DisabledInWindows;
        settings.Refresh();
        Assert.True(settings.IsDisabledInWindows);
        Assert.Contains(nameof(StartupSettings.IsDisabledInWindows), changes);
    }

    [Fact]
    public void RequiresARegistrationAndASettingsLauncher()
    {
        Assert.Throws<ArgumentNullException>(() => new StartupSettings(null!, () => { }));
        Assert.Throws<ArgumentNullException>(() => new StartupSettings(new FakeRegistration(), null!));
    }
}
