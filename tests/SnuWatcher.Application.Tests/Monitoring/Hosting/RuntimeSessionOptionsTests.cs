using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Monitoring.Scheduling;
using Xunit;

namespace SnuWatcher.Application.Tests.Monitoring.Hosting;

public sealed class RuntimeSessionOptionsTests
{
    [Fact]
    public void DefaultsKeepRefreshAndCommandOwnershipBoundedWithoutChangingSourcePolicy()
    {
        var monitor = new WatchMonitorOptions(maxUniqueSections: 23);
        var delivery = new BatchDeliveryPolicy(TimeSpan.FromMinutes(2), 3);
        var pump = new BatchDeliveryPumpOptions(maxWorkPerTurn: 7);
        var options = new RuntimeSessionOptions(monitor, delivery, pump);
        Assert.Same(monitor, options.Monitor);
        Assert.Same(delivery, options.Delivery);
        Assert.Same(pump, options.Pump);
        Assert.Equal(23, options.MaxRefreshSections);
        Assert.Equal(128, options.MaxAdmittedCommands);
        Assert.Equal(128, options.MaxPendingKnownResults);
        Assert.Equal(2, options.FinalizeFlushPasses);
        Assert.Equal(128, options.RecoveryPageSize);
        Assert.Equal(TimeSpan.FromSeconds(60), options.Monitor.TargetInterval);
    }

    [Fact]
    public void ResourceOptionsCannotExceedMonitorScopeOrAllocateUnboundedAdmission()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeSessionOptions(maxAdmittedCommands: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeSessionOptions(maxAdmittedCommands: 4097));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeSessionOptions(new(maxUniqueSections: 1), maxRefreshSections: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeSessionOptions(maxRefreshSections: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeSessionOptions(maxPendingKnownResults: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeSessionOptions(maxPendingKnownResults: 2001));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeSessionOptions(finalizeFlushPasses: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeSessionOptions(finalizeFlushPasses: 11));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeSessionOptions(recoveryPageSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeSessionOptions(recoveryPageSize: 1001));
    }
}
