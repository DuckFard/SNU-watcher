using SnuWatcher.Application.Monitoring.Scheduling;

namespace SnuWatcher.Application.Monitoring.Hosting;

/// <summary>Actual applied profile is distinct from a saved preference that is still awaiting application.</summary>
public interface IRuntimeRefreshProfile
{
    WatchMonitorProfileSnapshot AppliedRefreshProfile { get; }
    Task<WatchMonitorProfileSnapshot> SetRefreshProfileAsync(WatchMonitorProfile profile, long preferencesRevision,
        CancellationToken cancellationToken = default);
}
