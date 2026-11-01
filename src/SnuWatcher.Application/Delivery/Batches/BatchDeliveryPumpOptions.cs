namespace SnuWatcher.Application.Delivery.Batches;

/// <summary>Finite actor turns and memory; notification pacing does not change source polling policy.</summary>
public sealed record BatchDeliveryPumpOptions
{
    public BatchDeliveryPumpOptions(int pageSize = 128, int maxWorkPerTurn = 32, int maxFoldPages = 8,
        int maxTrackedSections = 4096, int shutdownFlushAttempts = 3)
    {
        if (pageSize is < 1 or > BatchDeliveryLimits.MaxPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (maxWorkPerTurn is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maxWorkPerTurn));
        if (maxFoldPages is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(maxFoldPages));
        if (maxTrackedSections is < 1 or > 65_536) throw new ArgumentOutOfRangeException(nameof(maxTrackedSections));
        if (shutdownFlushAttempts is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(shutdownFlushAttempts));
        PageSize = pageSize;
        MaxWorkPerTurn = maxWorkPerTurn;
        MaxFoldPages = maxFoldPages;
        MaxTrackedSections = maxTrackedSections;
        ShutdownFlushAttempts = shutdownFlushAttempts;
    }
    public int PageSize { get; }
    public int MaxWorkPerTurn { get; }
    public int MaxFoldPages { get; }
    public int MaxTrackedSections { get; }
    public int ShutdownFlushAttempts { get; }
    public TimeSpan IdleInterval => TimeSpan.FromSeconds(1);
    public TimeSpan RetryDelay => TimeSpan.FromSeconds(5);
    public TimeSpan FreshRequestInterval => TimeSpan.FromSeconds(5);
    public TimeSpan MinimumSubmissionInterval => TimeSpan.FromSeconds(1);
}
