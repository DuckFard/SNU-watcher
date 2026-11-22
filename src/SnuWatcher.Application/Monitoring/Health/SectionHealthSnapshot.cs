using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Monitoring.Health;

public enum SectionDataState { NeverChecked, Current, Delayed, Unavailable, ClockChanged }
public enum AvailabilityState { Unknown, Full, Open }

/// <summary>Pure read model. Historical counts do not imply current availability or personal eligibility.</summary>
public sealed record SectionHealthSnapshot
{
    internal SectionHealthSnapshot(SectionSnapshot section, DateTimeOffset evaluatedAtUtc, TimeSpan? age,
        TimeSpan staleThreshold, bool isStale, SectionDataState dataState, AvailabilityState availability)
    {
        Key = section.Key; EvaluatedAtUtc = evaluatedAtUtc;
        LastCompletedAttemptAtUtc = section.LatestAttemptAtUtc; LastSuccessfulAtUtc = section.LastSuccessfulAtUtc;
        LastKnownCounts = section.Baseline?.Seats; LatestOutcome = section.LatestOutcome;
        Age = age; StaleThreshold = staleThreshold; IsStale = isStale; DataState = dataState; Availability = availability;
    }
    public SectionKey Key { get; }
    public DateTimeOffset EvaluatedAtUtc { get; }
    /// <summary>Observation time of the latest completed check, not request issuance/start or database receipt time.</summary>
    public DateTimeOffset? LastCompletedAttemptAtUtc { get; }
    public DateTimeOffset? LastSuccessfulAtUtc { get; }
    public SeatCount? LastKnownCounts { get; }
    public CheckOutcome? LatestOutcome { get; }
    /// <summary>Age of last successful counts; null when absent or in the future, never a fabricated zero.</summary>
    public TimeSpan? Age { get; }
    public TimeSpan StaleThreshold { get; }
    /// <summary>A known nonfuture baseline exceeds the threshold. Absence/clock anomalies have separate states.</summary>
    public bool IsStale { get; }
    public SectionDataState DataState { get; }
    public AvailabilityState Availability { get; }
}
