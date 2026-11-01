using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Monitoring.Scheduling;

namespace SnuWatcher.Application.Monitoring.Hosting;

/// <summary>Immutable ownership/resource bounds; source pacing remains the monitor/source policy's responsibility.</summary>
public sealed record RuntimeSessionOptions
{
    public RuntimeSessionOptions(WatchMonitorOptions? monitor = null, BatchDeliveryPolicy? delivery = null,
        BatchDeliveryPumpOptions? pump = null, int maxAdmittedCommands = 128, int? maxRefreshSections = null,
        int maxPendingKnownResults = 128, int finalizeFlushPasses = 2, int recoveryPageSize = 128)
    {
        Monitor = monitor ?? new();
        Delivery = delivery ?? new(TimeSpan.FromMinutes(2), 3);
        Pump = pump ?? new();
        if (maxAdmittedCommands is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maxAdmittedCommands));
        var refresh = maxRefreshSections ?? Monitor.MaxUniqueSections;
        if (refresh < 1 || refresh > Monitor.MaxUniqueSections) throw new ArgumentOutOfRangeException(nameof(maxRefreshSections));
        if (maxPendingKnownResults is < 1 or > BatchDeliveryLimits.MaxRecipients) throw new ArgumentOutOfRangeException(nameof(maxPendingKnownResults));
        if (finalizeFlushPasses is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(finalizeFlushPasses));
        if (recoveryPageSize is < 1 or > DeliveryRecoveryLimits.MaxPageSize) throw new ArgumentOutOfRangeException(nameof(recoveryPageSize));
        MaxAdmittedCommands = maxAdmittedCommands;
        MaxRefreshSections = refresh;
        MaxPendingKnownResults = maxPendingKnownResults;
        FinalizeFlushPasses = finalizeFlushPasses;
        RecoveryPageSize = recoveryPageSize;
    }
    public WatchMonitorOptions Monitor { get; }
    public BatchDeliveryPolicy Delivery { get; }
    public BatchDeliveryPumpOptions Pump { get; }
    public int MaxAdmittedCommands { get; }
    public int MaxRefreshSections { get; }
    public int MaxPendingKnownResults { get; }
    public int FinalizeFlushPasses { get; }
    public int RecoveryPageSize { get; }
}
