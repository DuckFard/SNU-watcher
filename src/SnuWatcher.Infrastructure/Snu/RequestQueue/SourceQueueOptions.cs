namespace SnuWatcher.Infrastructure.Snu.RequestQueue;

/// <summary>Production pacing cannot be disabled through public source options.</summary>
internal sealed record SourceQueueOptions
{
    public SourceQueueOptions(TimeSpan requestSpacing, int capacity = 128) : this(requestSpacing, capacity, false) { }

    private SourceQueueOptions(TimeSpan requestSpacing, int capacity, bool allowZero)
    {
        if (capacity is < 1 or > 1024 || requestSpacing > TimeSpan.FromSeconds(60) ||
            (requestSpacing < TimeSpan.FromSeconds(2) && !(allowZero && requestSpacing == TimeSpan.Zero)))
            throw new ArgumentOutOfRangeException(nameof(requestSpacing));
        RequestSpacing = requestSpacing;
        Capacity = capacity;
    }

    public TimeSpan RequestSpacing { get; }
    public int Capacity { get; }
    internal static SourceQueueOptions ForTesting(TimeSpan spacing, int capacity = 128) => new(spacing, capacity, true);
}

internal sealed record SourceQueueSnapshot(bool AccessPaused, TimeSpan CooldownRemaining,
    int QueuedInteractive, int QueuedWatch, bool RequestInFlight)
{
    /// <summary>Last source-wide start fact, not a substitute for per-request provenance.</summary>
    public DateTimeOffset? LastTransportStartedAtUtc { get; init; }
}
