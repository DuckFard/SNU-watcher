using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Presentation.Diagnostics;
using SnuWatcher.Application.Presentation.Preferences;

namespace SnuWatcher.Windows.Diagnostics;

/// <summary>Maps potentially detailed runtime evidence to closed enums; raw codes are never copied to diagnostics.</summary>
public static class DiagnosticsStateProjector
{
    /// <summary>Copies scalar status and saved choices only; unrecognized reason strings become a closed fallback enum.</summary>
    public static DiagnosticsRuntimeFacts Project(RuntimeSessionState state, string? reason, bool? online, bool? suspended,
        RuntimeFailureKind? failure, WatchMonitorProfile? applied, UiPreferencesValues? saved, BatchChannelReadiness? readiness)
    {
        var source = failure == RuntimeFailureKind.SavedState ? DiagnosticsSourceCondition.StorageFailed
            : failure == RuntimeFailureKind.Actor ? DiagnosticsSourceCondition.ActorFailed
            : online == false ? DiagnosticsSourceCondition.Offline : suspended == true ? DiagnosticsSourceCondition.Suspended
            : reason switch
            {
                "source_cooldown" => DiagnosticsSourceCondition.Cooldown,
                "source_access_restricted" => DiagnosticsSourceCondition.AccessRestricted,
                null when online == true => DiagnosticsSourceCondition.NoKnownBlock,
                null => DiagnosticsSourceCondition.Unknown,
                _ => DiagnosticsSourceCondition.OtherBlocked,
            };
        var notifications = readiness switch
        {
            BatchChannelReadiness.Initializing => DiagnosticsNotificationCondition.Initializing,
            BatchChannelReadiness.Ready => DiagnosticsNotificationCondition.Ready,
            BatchChannelReadiness.Disabled => DiagnosticsNotificationCondition.Disabled,
            BatchChannelReadiness.Unavailable => DiagnosticsNotificationCondition.Unavailable,
            _ => DiagnosticsNotificationCondition.Unknown,
        };
        return new(state, source, notifications, applied,
            new(saved?.Language, saved?.DesiredProfile, saved?.CloseBehavior, saved?.NotificationSoundMuted), online, suspended);
    }
}

/// <summary>Captures existing scalar state and file metadata only; never invokes HTTP, SQLite or notification probes.</summary>
public sealed class WindowsDiagnosticsReader(IStorageDiagnosticsReader storage, Func<DiagnosticsRuntimeFacts> runtime, Version version, TimeProvider clock) : IDiagnosticsReader
{
    private readonly IStorageDiagnosticsReader storage = storage ?? throw new ArgumentNullException(nameof(storage));
    private readonly Func<DiagnosticsRuntimeFacts> runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    private readonly Version version = version ?? throw new ArgumentNullException(nameof(version));
    private readonly TimeProvider clock = clock ?? throw new ArgumentNullException(nameof(clock));
    /// <inheritdoc/>
    public async Task<DiagnosticsSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = runtime(); var at = clock.GetUtcNow();
        var footprint = await storage.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new(version, Environment.Version, at, state, footprint);
    }
}
