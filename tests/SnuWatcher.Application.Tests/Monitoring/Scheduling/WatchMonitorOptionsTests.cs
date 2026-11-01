using SnuWatcher.Application.Monitoring.Scheduling;
using Xunit;

namespace SnuWatcher.Application.Tests.Monitoring.Scheduling;

public sealed class WatchMonitorOptionsTests
{
    [Theory]
    [InlineData(WatchMonitorProfile.Faster, 30)]
    [InlineData(WatchMonitorProfile.Normal, 60)]
    [InlineData(WatchMonitorProfile.LowActivity, 120)]
    public void ProfilesHaveExplicitApplicationIntervals(WatchMonitorProfile profile, int seconds)
    {
        var options = new WatchMonitorOptions(profile);
        Assert.Equal(TimeSpan.FromSeconds(seconds), options.TargetInterval);
        Assert.Equal(4, options.MaxConcurrentQueries);
        Assert.Equal(2000, options.MaxUniqueSections);
        Assert.Equal(TimeSpan.FromMinutes(5), options.CatalogRefreshInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), options.CommitRetryDelay);
    }

    [Fact]
    public void ResourceAndTimingBoundsRejectUnsupportedInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatchMonitorOptions((WatchMonitorProfile)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatchMonitorOptions(maxConcurrentQueries: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatchMonitorOptions(maxConcurrentQueries: 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatchMonitorOptions(maxUniqueSections: 10001));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatchMonitorOptions(maxUniqueSections: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatchMonitorOptions(maxCommitAttempts: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatchMonitorOptions(maxCommitAttempts: 11));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatchMonitorOptions(jitterPercent: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatchMonitorOptions(jitterPercent: 11));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatchMonitorOptions(recoveryStagger: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatchMonitorOptions(catalogRefreshInterval: TimeSpan.FromSeconds(29)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatchMonitorOptions(commitRetryDelay: TimeSpan.Zero));
    }
}
