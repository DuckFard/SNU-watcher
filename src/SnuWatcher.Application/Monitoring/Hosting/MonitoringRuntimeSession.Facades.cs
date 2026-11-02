using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Application.Presentation.Reads;
using SnuWatcher.Application.Presentation.Terms;
using SnuWatcher.Application.Sources;

namespace SnuWatcher.Application.Monitoring.Hosting;

public sealed partial class MonitoringRuntimeSession : IRuntimeCourseQueries, IRuntimeWatchCommands,
    IRuntimeRefreshProfile, IWatchDashboardReader, IUiPreferencesStore, ITermHintCache
{
    private readonly ICourseSource _interactiveSource;
    private readonly RuntimePresentationServices? _presentationServices;
    public IRuntimeCourseQueries Courses => this;
    public IWatchDashboardReader Dashboard => this;
    public IUiPreferencesStore Preferences => this;
    public ITermHintCache TermHints => this;
    public WatchMonitorProfileSnapshot AppliedRefreshProfile => _monitor.AppliedRefreshProfile;

    public Task<WatchMonitorProfileSnapshot> SetRefreshProfileAsync(WatchMonitorProfile profile, long preferencesRevision,
        CancellationToken cancellationToken = default) =>
        AdmitUiOperation(token => _monitor.SetRefreshProfileAsync(profile, preferencesRevision, token), cancellationToken);

    Task<WatchPageReadResult> IWatchDashboardReader.ReadWatchPageAsync(WatchPageCursor? after, int pageSize, CancellationToken cancellationToken)
    {
        if (pageSize is < 1 or > WatchPageSnapshot.MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize));
        return AdmitPresentation((services, token) => services.Dashboard.ReadWatchPageAsync(after, pageSize, token), cancellationToken);
    }

    Task<WatchDetailsSnapshot?> IWatchDashboardReader.ReadWatchDetailsAsync(Guid subscriptionId, CancellationToken cancellationToken)
    {
        if (subscriptionId == Guid.Empty) throw new ArgumentException("A saved watch identity is required.", nameof(subscriptionId));
        return AdmitPresentation((services, token) => services.Dashboard.ReadWatchDetailsAsync(subscriptionId, token), cancellationToken, allowNull: true);
    }

    Task<SourceStatusSnapshot> IWatchDashboardReader.ReadSourceStatusAsync(CancellationToken cancellationToken) =>
        AdmitPresentation((services, token) => services.Dashboard.ReadSourceStatusAsync(token), cancellationToken);

    Task<UiPreferencesSnapshot> IUiPreferencesStore.ReadAsync(CancellationToken cancellationToken) =>
        AdmitPresentation((services, token) => services.Preferences.ReadAsync(token), cancellationToken);

    Task<UiPreferencesSaveResult> IUiPreferencesStore.TrySaveAsync(SaveUiPreferencesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return AdmitPresentation((services, token) => services.Preferences.TrySaveAsync(command, token), cancellationToken);
    }

    Task<TermHintCacheSnapshot> ITermHintCache.ReadAsync(CancellationToken cancellationToken) =>
        AdmitPresentation((services, token) => services.TermHints.ReadAsync(token), cancellationToken);

    Task<TermHintWriteResult> ITermHintCache.TryReplaceYearAsync(ReplaceTermHintsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return AdmitPresentation((services, token) => services.TermHints.TryReplaceYearAsync(command, token), cancellationToken);
    }
}
