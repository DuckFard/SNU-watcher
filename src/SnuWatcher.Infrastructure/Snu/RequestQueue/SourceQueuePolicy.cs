namespace SnuWatcher.Infrastructure.Snu.RequestQueue;

/// <summary>UTC is converted once at the HTTP boundary; elapsed monotonic time governs a running cooldown.</summary>
internal sealed record SourceQueuePolicy(bool AccessPaused = false, long CooldownStarted = 0,
    TimeSpan CooldownDuration = default)
{
    public TimeSpan Remaining(TimeProvider clock)
    {
        var elapsed = clock.GetElapsedTime(CooldownStarted, clock.GetTimestamp());
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return elapsed >= CooldownDuration ? TimeSpan.Zero : CooldownDuration - elapsed;
    }

    public SourceQueuePolicy Apply(SnuSourceException failure, TimeProvider clock)
    {
        if (failure.Kind == SourceFailureKind.AccessRestricted) return this with { AccessPaused = true };
        var delay = failure.Kind switch
        {
            SourceFailureKind.RateLimited => failure.RetryAfter ?? TimeSpan.FromSeconds(60),
            SourceFailureKind.Maintenance => failure.RetryAfter ?? TimeSpan.FromSeconds(120),
            SourceFailureKind.Unavailable when failure.HttpStatusCode == 503 => failure.RetryAfter,
            _ => null
        };
        return delay.HasValue ? Extend(delay.Value, clock) : this;
    }

    public SourceQueuePolicy Extend(TimeSpan duration, TimeProvider clock)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        var remaining = Remaining(clock);
        return this with { CooldownStarted = clock.GetTimestamp(), CooldownDuration = remaining > duration ? remaining : duration };
    }
}
