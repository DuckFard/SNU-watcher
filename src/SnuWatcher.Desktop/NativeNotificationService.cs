using System;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Windows.AppNotifications;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Desktop.Notifications;
using SnuWatcher.Windows;
using SnuWatcher.Windows.Hosting;
using SnuWatcher.Windows.Notifications;

namespace SnuWatcher.Desktop;

/// <summary>Native local notification boundary; accepted by Windows is not proof of a visible banner.</summary>
internal sealed class NativeNotificationService : IDisposable, IAsyncDisposable, IBatchDesktopAlertChannel, INativeNotificationControls
{
    private bool _registered;
    private bool _shortcutBound;
    private readonly NativeNotificationChannel _channel;
    private readonly RetryableTeardown _teardown;

    public NativeNotificationService()
    {
        _channel = new NativeNotificationChannel(new SdkNativeNotificationDriver(
            () => Volatile.Read(ref _registered), () => Volatile.Read(ref _shortcutBound)), diagnostic: ReportDiagnostic);
        _teardown = new RetryableTeardown(DisposeCoreAsync);
    }

    public event Action<ActivationRequest>? Activated;
    public event Action<string>? Diagnostic;
    public event Action? AvailabilityChanged
    {
        add => _channel.AvailabilityChanged += value;
        remove => _channel.AvailabilityChanged -= value;
    }
    public string Status { get; private set; } = "Not initialized";

    public void Initialize()
    {
        using var identity = WindowsIdentity.GetCurrent();
        NotificationElevationPolicy.EnsureUnelevated(
            new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
        NativeAppIdentity.Initialize();
        // Handler -> Register -> inspect lifecycle activation in App.OnStartup.
        AppNotificationManager.Default.NotificationInvoked += OnInvoked;
        try
        {
            AppNotificationManager.Default.Register();
            Volatile.Write(ref _registered, true);
            Volatile.Write(ref _shortcutBound, NativeShortcutBinding.BindExisting());
            Status = "Identity: " + NativeAppIdentity.ReadCurrent() +
                "; registered; Windows setting: " + AppNotificationManager.Default.Setting +
                "; installed shortcut: " + (_shortcutBound ? "identity verified" : "not found - install this candidate before testing");
        }
        catch
        {
            AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
            throw;
        }
    }

    public uint SendTest()
    {
        // Compatibility-only M2 call: keep its exact payload/pair, serialized with all production native work.
        // Student settings uses the asynchronous, localized INativeNotificationControls port instead.
        var receipt = _channel.SendLegacyTestAsync().GetAwaiter().GetResult();
        if (receipt.Result.Outcome != BatchChannelOutcome.OsAccepted || receipt.PlatformNotificationId is not { } id)
            throw new InvalidOperationException("Windows did not accept the test notification.");
        return id;
    }

    public bool ApplySavedPreferences(UiPreferencesSnapshot snapshot) => _channel.ApplySavedPreferences(snapshot);
    public Task<BatchChannelReadiness> ReadReadinessAsync(CancellationToken cancellationToken) => _channel.ReadReadinessAsync(cancellationToken);
    public Task<BatchChannelResult> SendAsync(BatchDesktopSubmission submission, CancellationToken cancellationToken) => _channel.SendAsync(submission, cancellationToken);
    public Task<NativeNotificationStatus> ReadStatusAsync(CancellationToken cancellationToken = default) => _channel.ReadStatusAsync(cancellationToken);
    public Task<NativeNotificationTestReceipt> SendTestAsync(CancellationToken cancellationToken = default) => _channel.SendTestAsync(cancellationToken);

    private void OnInvoked(AppNotificationManager manager, AppNotificationActivatedEventArgs arguments)
    {
        try { Activated?.Invoke(ActivationRequest.Parse(arguments.Argument)); }
        catch (ArgumentException) { ReportDiagnostic("Unrecognized notification activation was ignored."); }
        catch { ReportDiagnostic("native_activation_observer_failed"); }
    }

    public async Task RemoveRegistrationAsync()
    {
        await _channel.DisposeAsync().ConfigureAwait(false);
        // UnregisterAll removes registration data; clearing retained toasts is a separate API.
        // Await completion before the installer is allowed to remove this executable.
        await NotificationCleanupPolicy.ClearAndUnregisterAsync(_registered,
            () => AppNotificationManager.Default.RemoveAllAsync().AsTask(),
            () => AppNotificationManager.Default.UnregisterAll(),
            NotificationCleanupPolicy.MaximumRemovalWait);
        Volatile.Write(ref _registered, false);
        Status = "Retained notifications and notification registration removed";
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    // A failed completed Unregister leaves registration owned. Retry it explicitly while sharing any running attempt.
    public ValueTask DisposeAsync() => new(_teardown.RunAsync(retryFailed: true));

    private async Task DisposeCoreAsync()
    {
        // Registration/resources remain owned until every actual SDK test/probe/Show has returned.
        await _channel.DisposeAsync().ConfigureAwait(false);
        AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
        if (_registered)
        {
            AppNotificationManager.Default.Unregister();
            Volatile.Write(ref _registered, false);
        }
    }

    private void ReportDiagnostic(string message)
    {
        try { Diagnostic?.Invoke(message); } catch { /* Native evidence cannot depend on a diagnostic observer. */ }
    }
}
