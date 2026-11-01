namespace SnuWatcher.Application.Monitoring.Scheduling;

public enum WatchMonitorProfile { Faster, Normal, LowActivity }

/// <summary>Application policy and resource bounds; none imply an institution-approved source rate.</summary>
public sealed record WatchMonitorOptions
{
    public WatchMonitorOptions(WatchMonitorProfile profile = WatchMonitorProfile.Normal, int maxConcurrentQueries = 4,
        int maxUniqueSections = 2000, int maxCommitAttempts = 3, int jitterPercent = 5,
        TimeSpan? recoveryStagger = null, TimeSpan? catalogRefreshInterval = null, TimeSpan? commitRetryDelay = null)
    {
        if (!Enum.IsDefined(profile)) throw new ArgumentOutOfRangeException(nameof(profile));
        if (maxConcurrentQueries is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(maxConcurrentQueries));
        if (maxUniqueSections is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(maxUniqueSections));
        if (maxCommitAttempts is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(maxCommitAttempts));
        if (jitterPercent is < 0 or > 10) throw new ArgumentOutOfRangeException(nameof(jitterPercent));
        Profile = profile;
        MaxConcurrentQueries = maxConcurrentQueries;
        MaxUniqueSections = maxUniqueSections;
        MaxCommitAttempts = maxCommitAttempts;
        JitterPercent = jitterPercent;
        RecoveryStagger = Bound(recoveryStagger ?? TimeSpan.FromSeconds(2), 2, 30, nameof(recoveryStagger));
        CatalogRefreshInterval = Bound(catalogRefreshInterval ?? TimeSpan.FromMinutes(5), 30, 86400, nameof(catalogRefreshInterval));
        CommitRetryDelay = Bound(commitRetryDelay ?? TimeSpan.FromSeconds(5), 1, 60, nameof(commitRetryDelay));
    }

    public WatchMonitorProfile Profile { get; }
    public TimeSpan TargetInterval => TimeSpan.FromSeconds(Profile switch
        { WatchMonitorProfile.Faster => 30, WatchMonitorProfile.Normal => 60, _ => 120 });
    public int MaxConcurrentQueries { get; }
    public int MaxUniqueSections { get; }
    public int MaxCommitAttempts { get; }
    public int JitterPercent { get; }
    public TimeSpan RecoveryStagger { get; }
    public TimeSpan CatalogRefreshInterval { get; }
    public TimeSpan CommitRetryDelay { get; }

    private static TimeSpan Bound(TimeSpan value, int minimumSeconds, int maximumSeconds, string name) =>
        value >= TimeSpan.FromSeconds(minimumSeconds) && value <= TimeSpan.FromSeconds(maximumSeconds)
            ? value : throw new ArgumentOutOfRangeException(name);
}
