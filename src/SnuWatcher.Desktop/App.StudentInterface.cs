using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SnuWatcher.Desktop.Presentation;
using SnuWatcher.Windows;
using SnuWatcher.Windows.Hosting;
using SnuWatcher.Windows.Presentation.Settings;
using SnuWatcher.Windows.Updates;

namespace SnuWatcher.Desktop;

public partial class App
{
    private readonly CancellationTokenSource _studentStartupStop = new();
    private readonly CancellationTokenSource _studentLifetimeStop = new();
    private WindowsMonitoringHost? _studentHost;
    private StudentDesktopSession? _studentSession;
    private StudentWindow? _studentWindow;
    private Task<WindowsHostOpenResult>? _studentOpening;
    private Task? _studentStarting;
    private Task? _studentQuit;
    private Task? _studentFailureObserver;
    private RetryableTeardown? _studentTeardown;
    private bool _useStudentInterface;
    // Started by Windows at sign-in: stay in the tray instead of opening the window.
    private bool _startInBackground;

    /// <summary>
    /// The sign-in start switch for this installed exe. A run through dotnet.exe (developer builds) gets none, so it
    /// can never register another program to start at sign-in.
    /// </summary>
    private static StartupSettings? CreateStartupSettings()
    {
        var path = Environment.ProcessPath;
        if (path is null || !string.Equals(Path.GetFileName(path), "SnuWatcher.Desktop.exe", StringComparison.OrdinalIgnoreCase))
            return null;
        return new StartupSettings(new RunKeyStartupRegistration(path), OpenWindowsStartupSettings);
    }

    /// <summary>
    /// The new-version check for this installed exe, like the sign-in switch above: developer builds run through
    /// dotnet.exe, and builds whose version is not a release version, get none.
    /// </summary>
    private static UpdateSettings? CreateUpdateSettings()
    {
        var path = Environment.ProcessPath;
        if (path is null || !string.Equals(Path.GetFileName(path), "SnuWatcher.Desktop.exe", StringComparison.OrdinalIgnoreCase)
            || !ReleaseVersion.TryParse(AppAbout.Version, out var current))
            return null;
        return new UpdateSettings(new RegistryUpdateCheckStore(), new GitHubUpdateSource(current!), current!, TimeProvider.System,
            OpenReleasePage);
    }

    private static void OpenReleasePage(Uri page)
    {
        if (IsReleasePage(page)) AppAbout.TryOpen(page.AbsoluteUri);
    }

    /// <summary>Built by GitHubReleaseFeed.ReleasePage from a checked version; anything else is refused.</summary>
    internal static bool IsReleasePage(Uri page) =>
        page.Scheme == Uri.UriSchemeHttps && page.Host == "github.com"
        && page.AbsolutePath.StartsWith("/DuckFard/SNU-watcher/releases/tag/v", StringComparison.Ordinal);

    private static void OpenWindowsStartupSettings()
    {
        // A fixed Windows Settings page; nothing from the network or the user becomes part of this command.
        try { using var _ = Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true }); }
        catch (Win32Exception) { /* Windows Settings is unavailable; the card still explains where the switch is. */ }
    }

    private Task StartStudentInterfaceAsync()
    {
        Dispatcher.VerifyAccess();
        return _studentStarting ??= StartStudentInterfaceCoreAsync();
    }

    private async Task StartStudentInterfaceCoreAsync()
    {
        if (_quitting) return;
        try
        {
            _studentOpening = _notifications is null
                ? WindowsMonitoringHost.OpenMonitoringOnlyProductionAsync(cancellationToken: _studentStartupStop.Token)
                : WindowsMonitoringHost.OpenProductionAsync(_notifications, cancellationToken: _studentStartupStop.Token);
            var opened = await _studentOpening;
            if (_quitting) return;
            _studentHost = opened.Host;
            if (_studentHost is null)
            {
                // Another copy already owns the watches; a sign-in start has nothing to show.
                if (_startInBackground) { Shutdown(); return; }
                _studentWindow = new(null, Quit, enableTray: false);
                MainWindow = _studentWindow;
                _studentWindow.ShowStatusKey("Ui.AlreadyRunning");
                _studentWindow.Show();
                return;
            }
            var ui = new WpfUiDispatcher(Dispatcher);
            _studentSession = new(_studentHost, ui, TimeProvider.System, new DesktopStudentActions(ui), SendTestNotification,
                nativeNotifications: _notifications, startup: CreateStartupSettings(), updates: CreateUpdateSettings());
            AttachStudentNotifications();
            _studentWindow = new(_studentSession.Shell, Quit);
            MainWindow = _studentWindow;
            // The tray icon exists either way; a sign-in start opens the window only when the student asks.
            if (!_startInBackground) _studentWindow.Show();
            _studentFailureObserver = ObserveStudentFailureAsync(_studentSession, _studentHost);
            await _studentSession.InitializeAsync(_studentStartupStop.Token);
        }
        catch (OperationCanceledException) when (_quitting) { /* The owned quit path drains startup. */ }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (error is WindowsMonitoringHostStartupException retained) _studentHost = retained.Host;
            var drained = await DrainStudentResourcesAsync();
            if (drained)
            {
                MessageBox.Show("SNU Watcher could not open its saved state or interface. Your existing data was preserved.",
                    "SNU Watcher", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(2);
            }
            else
            {
                _status = "Startup could not finish saving its state. Choose Quit to retry safe finalization.";
                if (_studentWindow is not null) _studentWindow.ShowStatusKey("Ui.StopBlocked");
                else { _window = new MainWindow(SendTestNotification, Quit); MainWindow = _window; _window.SetStatus(_status); _window.Show(); }
            }
        }
    }

    private bool ShowStudentWindow()
    {
        Window? target = _studentWindow ?? (Window?)_window;
        if (target is null) return false; // Startup still owns its asynchronous acquisition.
        target.Show();
        if (target.WindowState == WindowState.Minimized) target.WindowState = WindowState.Normal;
        return target.Activate();
    }

    private async Task ObserveStudentFailureAsync(StudentDesktopSession session, WindowsMonitoringHost host)
    {
        Task failed;
        try
        {
            failed = await Task.WhenAny(session.FailureTask, (Task)host.Session.FailureTask, _studentNativeFailure.Task)
                .WaitAsync(_studentLifetimeStop.Token);
        }
        catch (OperationCanceledException) when (_studentLifetimeStop.IsCancellationRequested) { return; }
        if (_quitting) return;
        _studentWindow?.SetStopping();
        var drained = await DrainStudentResourcesAsync();
        if (_quitting) return;
        var issue = failed == host.Session.FailureTask
            && (await host.Session.FailureTask).Kind == SnuWatcher.Application.Monitoring.Hosting.RuntimeFailureKind.SavedState
            ? "Ui.SavedStateError" : "Ui.RuntimeError";
        _studentWindow?.ShowStatusKey(drained ? issue : "Ui.StopBlocked");
        ShowStudentWindow();
    }

    private void BeginStudentQuit()
    {
        if (_quitting) return;
        _quitting = true;
        _studentWindow?.SetStopping();
        _studentQuit = QuitStudentCoreAsync();
    }

    private async Task QuitStudentCoreAsync()
    {
        try
        {
            var stopStartup = _studentStartupStop.CancelAsync();
            var stopObserver = _studentLifetimeStop.CancelAsync();
            var drained = await DrainStudentResourcesAsync(retryFailed: true);
            await Task.WhenAll(stopStartup, stopObserver, _studentFailureObserver ?? Task.CompletedTask);
            if (!drained) { _quitting = false; _studentWindow?.ShowStatusKey("Ui.StopBlocked"); return; }
            _studentWindow?.AllowClose(); _window?.AllowClose();
            Shutdown();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine("SNU Watcher retained its shutdown state: " + error.GetType().Name);
            _quitting = false;
            _studentWindow?.ShowStatusKey("Ui.StopBlocked");
        }
    }

    private Task<bool> DrainStudentResourcesAsync(bool retryFailed = false) =>
        (_studentTeardown ??= new(DrainStudentCoreAsync)).TryRunAsync(retryFailed);

    private async Task DrainStudentCoreAsync()
    {
        DetachStudentNotifications();
        if (_studentOpening is not null)
        {
            try { _studentHost ??= (await _studentOpening).Host; }
            catch (WindowsMonitoringHostStartupException retained) { _studentHost = retained.Host; }
            catch (Exception error) when (error is not OutOfMemoryException) { /* Factory already unwound ordinary startup failure. */ }
        }
        var uiDrain = DrainInterfaceAsync();
        var hostDrain = DrainHostAsync();
        // Start both owners before waiting: a UI driver can still be awaiting a runtime operation.
        await Task.WhenAll(uiDrain, hostDrain);
        if (!await hostDrain) throw new InvalidOperationException("Student runtime finalization remains blocked.");
        if (_studentHost is not null)
            await _studentHost!.DisposeAsync(); // Join the concrete host's stop worker and failure observer too.
        // Retain registration while logical finalization is blocked; no callback/SDK call is abandoned.
        if (_notifications is not null) await _notifications.DisposeAsync();

        // Async wrappers retain even synchronous finalizer failures, so the other owner is still started
        // and awaited before the attempt is reported blocked. Neither wrapper releases a blocked owner.
        async Task DrainInterfaceAsync()
        {
            if (_studentSession is not null) await _studentSession.DisposeAsync();
        }
        async Task<bool> DrainHostAsync()
        {
            if (_studentHost is null) return true;
            var result = _studentHost.State == WindowsMonitoringHostState.StopBlocked
                ? await _studentHost.RetryFinalizeStopAsync() : await _studentHost.StopAsync();
            return result.IsFinalized;
        }
    }
}
