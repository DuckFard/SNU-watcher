using SnuWatcher.Application.Delivery;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Application.Presentation.Terms;

namespace SnuWatcher.Windows.Hosting;

public sealed partial class WindowsMonitoringHost
{
    /// <summary>Owned interactive queries over the same source as monitoring, never the raw gateway.</summary>
    public IRuntimeCourseQueries Courses => _session.Courses;
    /// <summary>Bounded, consistent dashboard reads admitted and drained by the session.</summary>
    public IWatchDashboardReader Dashboard => _session.Dashboard;
    /// <summary>Owned preference reads/saves; this view never exposes the injected store.</summary>
    public IUiPreferencesStore Preferences => _session.Preferences;
    /// <summary>Owned nonauthoritative offline term hints, distinct from a live source catalog.</summary>
    public ITermHintCache TermHints => _session.TermHints;
    /// <summary>Watch lifecycle commands share the session's delivery gate and command ownership.</summary>
    public IRuntimeWatchCommands WatchCommands => _session;
    /// <summary>Applied profile state and revision-ordered live changes, without restarting the host.</summary>
    public IRuntimeRefreshProfile RefreshProfile => _session;

    /// <summary>
    /// Opens the fixed normal profile with monitoring enabled and desktop delivery forcibly disabled.
    /// Pending alerts remain durable for later integration; no native sender or fake acknowledgement is used.
    /// </summary>
    public static Task<WindowsHostOpenResult> OpenMonitoringOnlyProductionAsync(RuntimeSessionOptions? options = null,
        TimeProvider? clock = null, CancellationToken cancellationToken = default) =>
        OpenMonitoringOnlyCoreAsync(RuntimeProfile.Production(), new(OpenStoreAsync, OpenProductionSourceAsync),
            clock ?? TimeProvider.System, options ?? new(), cancellationToken);

    internal static Task<WindowsHostOpenResult> OpenMonitoringOnlyForTestingAsync(RuntimeProfile profile,
        WindowsMonitoringHostFactories factories, RuntimeSessionOptions? options = null, TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(factories);
        if (!profile.IsValidation) throw new ArgumentException("Tests cannot select the normal profile.", nameof(profile));
        return OpenMonitoringOnlyCoreAsync(profile, factories, clock ?? TimeProvider.System, options ?? new(), cancellationToken);
    }

    private static Task<WindowsHostOpenResult> OpenMonitoringOnlyCoreAsync(RuntimeProfile profile,
        WindowsMonitoringHostFactories factories, TimeProvider clock, RuntimeSessionOptions options, CancellationToken ct) =>
        OpenCoreAsync(profile, MonitoringOnlyChannel.Instance, factories, clock, options.WithDeliveryEnabled(false), ct);

    private sealed class MonitoringOnlyChannel : IBatchDesktopAlertChannel
    {
        internal static MonitoringOnlyChannel Instance { get; } = new();
        public Task<DesktopAlertOutcome> SendAsync(BatchDeliveryEnvelope envelope, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Desktop alert delivery is disabled in monitoring-only mode.");
    }
}
