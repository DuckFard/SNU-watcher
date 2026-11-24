using Microsoft.Win32;
using Xunit;

namespace SnuWatcher.Windows.Tests;

/// <summary>Runs the real registry logic under a throwaway per-test key; the user's actual Run key is never touched.</summary>
public sealed class StartupRegistrationTests : IDisposable
{
    private const string Exe = @"C:\Users\Student\AppData\Local\Programs\DuckFard\SNU Watcher\SnuWatcher.Desktop.exe";
    private const string Parent = @"Software\SnuWatcherTests";
    private readonly string root = Parent + @"\" + Guid.NewGuid().ToString("N");
    private string RunPath => root + @"\Run";
    private string ApprovedPath => root + @"\Approved";

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        using (var parent = Registry.CurrentUser.OpenSubKey(Parent))
            if (parent is null || parent.SubKeyCount > 0 || parent.ValueCount > 0) return;
        Registry.CurrentUser.DeleteSubKey(Parent, throwOnMissingSubKey: false);
    }

    private RunKeyStartupRegistration Create(string exe = Exe) => new(Registry.CurrentUser, RunPath, ApprovedPath, exe);

    private object? RunValue()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunPath);
        return run?.GetValue(RunKeyStartupRegistration.ValueName);
    }

    private void SetApproved(params byte[] state)
    {
        using var approved = Registry.CurrentUser.CreateSubKey(ApprovedPath);
        approved.SetValue(RunKeyStartupRegistration.ValueName, state, RegistryValueKind.Binary);
    }

    [Fact]
    public void EnableWritesTheQuotedExecutableWithTheBackgroundArgumentAndDisableRemovesIt()
    {
        var registration = Create();
        Assert.Equal(StartupRegistrationState.Off, registration.Read());

        registration.Enable();
        Assert.Equal("\"" + Exe + "\" --background", RunValue());
        Assert.Equal(registration.Command, RunValue());
        Assert.Equal(StartupRegistrationState.On, registration.Read());

        registration.Enable();
        Assert.Equal(StartupRegistrationState.On, registration.Read());

        registration.Disable();
        Assert.Null(RunValue());
        Assert.Equal(StartupRegistrationState.Off, registration.Read());
        registration.Disable();
    }

    [Fact]
    public void AnEntryForAnotherCopyOfTheAppIsNotThisAppsStartAndIsReplacedOnEnable()
    {
        Create(@"D:\Old\SnuWatcher.Desktop.exe").Enable();
        var registration = Create();
        Assert.Equal(StartupRegistrationState.Off, registration.Read());

        registration.Enable();
        Assert.Equal(registration.Command, RunValue());
        Assert.Equal(StartupRegistrationState.On, registration.Read());
    }

    [Fact]
    public void TheUsersSwitchInWindowsIsReportedAndNeverChanged()
    {
        var registration = Create();
        registration.Enable();
        SetApproved(0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(StartupRegistrationState.DisabledInWindows, registration.Read());

        // Enabling again keeps the user's Windows choice; only Windows' own settings turn it back on.
        registration.Enable();
        Assert.Equal(StartupRegistrationState.DisabledInWindows, registration.Read());
        using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedPath))
            Assert.Equal(0x03, ((byte[])approved!.GetValue(RunKeyStartupRegistration.ValueName)!)[0]);

        SetApproved(0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(StartupRegistrationState.On, registration.Read());
        SetApproved();
        Assert.Equal(StartupRegistrationState.On, registration.Read());
    }

    [Fact]
    public void ASwitchRecordedWithoutAnEntryDoesNotTurnItOn()
    {
        SetApproved(0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(StartupRegistrationState.Off, Create().Read());
    }

    [Theory]
    [InlineData("SnuWatcher.Desktop.exe")]
    [InlineData(@"C:\Apps\Snu""Watcher.exe")]
    [InlineData("")]
    public void OnlyAnAbsoluteExecutablePathCanBeRegistered(string exe) =>
        Assert.ThrowsAny<ArgumentException>(() => Create(exe));

    [Fact]
    public void TheProductionValueNameMatchesTheInstallersUninstallCleanup()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "installer", "SnuWatcher.iss"));
        foreach (var subkey in new[] { @"Software\Microsoft\Windows\CurrentVersion\Run",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run" })
            Assert.Contains("Root: HKCU; Subkey: \"" + subkey + "\"; ValueType: none; ValueName: \""
                + RunKeyStartupRegistration.ValueName + "\"; Flags: uninsdeletevalue", script);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SnuWatcher.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
