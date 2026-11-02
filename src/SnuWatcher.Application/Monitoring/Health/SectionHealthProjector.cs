using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Monitoring.Health;

/// <summary>No I/O or clock access. Call with the same evaluation instant for all members of a group.</summary>
public static class SectionHealthProjector
{
    public static SectionHealthSnapshot Project(SectionSnapshot section, DateTimeOffset now, TimeSpan requestedTarget)
    {
        ArgumentNullException.ThrowIfNull(section);
        var threshold = Threshold(requestedTarget);
        now = now.ToUniversalTime();
        var baseline = section.Baseline;
        TimeSpan? age = baseline is not null && baseline.ObservedAt <= now ? now - baseline.ObservedAt : null;
        var stale = age is { } elapsed && elapsed > threshold;
        var state = State(section, now, stale);
        var availability = state == SectionDataState.Current
            ? baseline!.Seats.ApparentSeats > 0 ? AvailabilityState.Open : AvailabilityState.Full : AvailabilityState.Unknown;
        return new(section, now, age, threshold, stale, state, availability);
    }

    internal static TimeSpan Threshold(TimeSpan requestedTarget)
    {
        if (requestedTarget < TimeSpan.FromSeconds(30) || requestedTarget > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(requestedTarget), "The requested target must be between 30 seconds and one hour.");
        return TimeSpan.FromTicks(Math.Max(requestedTarget.Ticks * 2, TimeSpan.FromSeconds(120).Ticks));
    }

    private static SectionDataState State(SectionSnapshot section, DateTimeOffset now, bool stale)
    {
        if (section.LatestCompletedOrdinal == 0) return SectionDataState.NeverChecked;
        if (section.LatestAttemptAtUtc > now || section.LastSuccessfulAtUtc > now) return SectionDataState.ClockChanged;
        if (section.LatestOutcome != CheckOutcome.Success || section.Baseline is not { } baseline
            || baseline.Meaning != QuotaMeaning.EnrolledOverQuota) return SectionDataState.Unavailable;
        return stale ? SectionDataState.Delayed : SectionDataState.Current;
    }
}
