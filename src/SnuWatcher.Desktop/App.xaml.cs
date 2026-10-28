using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using SnuWatcher.Windows;

namespace SnuWatcher.Desktop;

public partial class App : Application
{
    private SingleInstanceCoordinator? _instance;
    private NativeNotificationService? _notifications;
    private MainWindow? _window;
    private bool _owner;
    private bool _coldLaunch;
    private bool _quitting;
    private bool _testSucceeded;
    private string _status = "Native notifications not initialized";

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        try
        {
            _coldLaunch = ActivationRequest.IsNotificationLaunch(e.Args);
            _instance = new SingleInstanceCoordinator("DuckFard.SnuWatcher");
            _owner = _instance.TryAcquire();
            if (e.Args.Contains("--quit", StringComparer.Ordinal))
            {
                if (!_owner)
                {
                    var processId = await _instance.ForwardAsync(new ActivationRequest(ActivationAction.Quit), CancellationToken.None);
                    using var quitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try
                    {
                        using var ownerProcess = Process.GetProcessById(processId);
                        await ownerProcess.WaitForExitAsync(quitDeadline.Token);
                    }
                    catch (ArgumentException) { /* Owner exited between acknowledgement and process lookup. */ }
                }
                Shutdown();
                return;
            }
            if (!_owner && !_coldLaunch)
            {
                if (e.Args.Any(arg => arg is "--diagnostics" or "--test-notification" or "--unregister-notifications"))
                {
                    NativeDiagnostics.Write("operation-rejected", "Quit the existing candidate instance before running native diagnostics or cleanup.", false);
                    Shutdown(2);
                    return;
                }
                await _instance.ForwardAsync(new ActivationRequest(ActivationAction.Show), CancellationToken.None);
                Shutdown();
                return;
            }
            if (_owner)
            {
                _instance.Diagnostic += message => Dispatcher.BeginInvoke(() => UpdateStatus(message));
                _instance.StartListening(request => Dispatcher.BeginInvoke(() => HandleActivation(request)));
            }
            _notifications = new NativeNotificationService();
            _notifications.Activated += request => Dispatcher.BeginInvoke(() => HandleActivation(request));
            _notifications.Diagnostic += message => Dispatcher.BeginInvoke(() => UpdateStatus(message));
            AppActivationArguments? startupActivation = null;
            var notificationStartupStage = "registration";
            try
            {
                _notifications.Initialize();
                // Required ordering: notification handler -> Register() -> lifecycle activation.
                notificationStartupStage = "lifecycle";
                NativeDiagnostics.Write("lifecycle-reading", "Native registration complete; retrieving startup activation.", _owner);
                startupActivation = AppInstance.GetCurrent().GetActivatedEventArgs();
                NativeDiagnostics.Write("lifecycle-read", "Startup activation kind: " + startupActivation.Kind, _owner);
                _status = _notifications.Status;
                NativeDiagnostics.Write("registered", _status, _owner);
            }
            catch (Exception error)
            {
                _status = $"Native {notificationStartupStage} failed: {error.GetType().Name} (0x{error.HResult:X8}). " +
                    (error is UnsupportedNotificationElevationException
                        ? NotificationElevationPolicy.Guidance
                        : error is NativeShortcutRegistrationException shortcutError
                            ? shortcutError.Diagnostic
                            : "Inspect the native validation guide. Registration success alone does not establish banner visibility.");
                NativeDiagnostics.Write(notificationStartupStage + "-failed", _status, _owner);
                if (e.Args.Contains("--diagnostics", StringComparer.Ordinal))
                {
                    Shutdown(2);
                    return;
                }
            }
            if (e.Args.Contains("--unregister-notifications", StringComparer.Ordinal))
            {
                await _notifications.RemoveRegistrationAsync();
                NativeDiagnostics.Write("unregistered", _notifications.Status, _owner);
                Shutdown();
                return;
            }
            if (e.Args.Contains("--diagnostics", StringComparer.Ordinal))
            {
                Shutdown();
                return;
            }
            if (e.Args.Contains("--test-notification", StringComparer.Ordinal))
            {
                SendTestNotification();
                var exitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                exitTimer.Tick += (_, _) => { exitTimer.Stop(); Shutdown(_testSucceeded ? 0 : 2); };
                exitTimer.Start();
                return;
            }
            var isNotificationActivation = startupActivation?.Kind == ExtendedActivationKind.AppNotification;
            if (isNotificationActivation) _coldLaunch = true;
            try
            {
                // The SDK stores the first cold payload in lifecycle Data rather than raising NotificationInvoked.
                // Later clicks are delivered through the already registered NotificationInvoked handler.
                if (NotificationActivationRouter.DispatchStartup(isNotificationActivation,
                    (startupActivation?.Data as AppNotificationActivatedEventArgs)?.Argument,
                    HandleActivation))
                    return;
            }
            catch (ArgumentException)
            {
                NativeDiagnostics.Write("activation-rejected", "Startup notification payload was missing or unrecognized.", _owner);
                Shutdown(2);
                return;
            }
            if (_coldLaunch)
            {
                NativeDiagnostics.Write("activation-missing", "Windows notification launch did not provide notification lifecycle data.", _owner);
                Shutdown(2);
                return;
            }
            ShowWindow();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            try { NativeDiagnostics.Write("startup-failed", error.GetType().Name, _owner); }
            catch (IOException) { /* Startup still reports a failure when local diagnostics cannot be written. */ }
            if (!e.Args.Any(arg => arg.StartsWith("--", StringComparison.Ordinal)))
                MessageBox.Show("SNU Watcher could not start. " + error.GetType().Name +
                    ". Close another instance if necessary and retry.", "SNU Watcher", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
        }
    }

    private async void HandleActivation(ActivationRequest request)
    {
        NativeDiagnostics.Write("activation-received", "Validated native/local action: " + request.Action +
            "; cold startup: " + _coldLaunch, _owner);
        if (!_owner && _instance is not null)
        {
            try { await _instance.ForwardAsync(request, CancellationToken.None); }
            catch (Exception error) when (error is IOException or OperationCanceledException)
            { NativeDiagnostics.Write("activation-forward-failed", error.GetType().Name, false); }
            Shutdown();
            return;
        }
        if (request.Action == ActivationAction.Quit)
        {
            Quit();
            return;
        }
        if (request.Action == ActivationAction.OpenRegistration)
        {
            try
            {
                Process.Start(new ProcessStartInfo(OfficialDestinations.Registration.AbsoluteUri) { UseShellExecute = true });
                NativeDiagnostics.Write("activation", "Official registration opened", _owner);
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                NativeDiagnostics.Write("activation-browser-failed", error.GetType().Name + $" (0x{error.HResult:X8})", _owner);
                UpdateStatus("The default browser could not be opened. Use the official SNU registration website.");
                ShowWindow();
            }
            if (_coldLaunch && _window is null) Shutdown();
            return;
        }
        ShowWindow();
        UpdateStatus(request.Action == ActivationAction.ViewSection
            ? "Notification activation received. Section views arrive after the M2 gate."
            : "Existing instance brought to foreground.");
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow(SendTestNotification, Quit);
            MainWindow = _window;
        }
        _window.SetStatus(_status);
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void SendTestNotification()
    {
        try
        {
            var id = _notifications?.SendTest() ?? throw new InvalidOperationException("Native notifications are unavailable.");
            _testSucceeded = true;
            UpdateStatus($"Windows accepted test notification {id}. Confirm the banner and click; acceptance does not prove visibility.");
            NativeDiagnostics.Write("test-submitted", _status, _owner, id);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _testSucceeded = false;
            UpdateStatus("Test notification failed: " + error.GetType().Name + ". Check Windows notification settings and the validation guide.");
            NativeDiagnostics.Write("test-failed", _status, _owner);
        }
    }

    private void UpdateStatus(string message)
    {
        _status = message;
        _window?.SetStatus(message);
    }

    private void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _window?.AllowClose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _window?.Dispose();
        _notifications?.Dispose();
        _instance?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
