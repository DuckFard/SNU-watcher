using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace SnuWatcher.Integration.Tests;

/// <summary>UTC can move independently while the delegated timer/timestamp timeline remains monotonic.</summary>
internal sealed class QueueClock : TimeProvider
{
    private readonly FakeTimeProvider timers = new(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly Channel<TimeSpan> scheduled = Channel.CreateUnbounded<TimeSpan>();
    private long utcOffsetTicks;
    private readonly long origin;
    private readonly Action<TimeSpan>? beforeTimerCreation;

    public QueueClock(Action<TimeSpan>? beforeTimerCreation = null)
    {
        origin = timers.GetTimestamp();
        this.beforeTimerCreation = beforeTimerCreation;
    }
    public override DateTimeOffset GetUtcNow() => timers.GetUtcNow().AddTicks(Interlocked.Read(ref utcOffsetTicks));
    public override long TimestampFrequency => timers.TimestampFrequency;
    public override long GetTimestamp() => timers.GetTimestamp() - origin;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        beforeTimerCreation?.Invoke(dueTime);
        var timer = timers.CreateTimer(callback, state, dueTime, period);
        if (dueTime != Timeout.InfiniteTimeSpan) scheduled.Writer.TryWrite(dueTime);
        return timer;
    }
    public void Advance(TimeSpan duration) => timers.Advance(duration);
    public void AdjustUtc(TimeSpan difference) => Interlocked.Add(ref utcOffsetTicks, difference.Ticks);
    public async Task<TimeSpan> NextTimerAsync(TimeSpan? expected = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var duration = await scheduled.Reader.ReadAsync(deadline.Token);
            if (expected is null || duration == expected) return duration;
        }
    }
}
