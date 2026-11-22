using System.Collections.Immutable;
using SnuWatcher.Application.Persistence;

namespace SnuWatcher.Application.Monitoring.Health;

/// <summary>A current membership projection. Watch lifecycle and source data health remain independent.</summary>
public sealed record GroupHealthSnapshot
{
    internal GroupHealthSnapshot(Guid subscriptionId, WatchStatus effectiveWatchStatus,
        ImmutableArray<SectionHealthSnapshot> members, DateTimeOffset evaluatedAtUtc)
    {
        SubscriptionId = subscriptionId; EffectiveWatchStatus = effectiveWatchStatus; Members = members; EvaluatedAtUtc = evaluatedAtUtc;
        CoverageCount = members.Count(m => m.DataState == SectionDataState.Current);
        Availability = members.Any(m => m.Availability == AvailabilityState.Open) ? AvailabilityState.Open
            : members.All(m => m.Availability == AvailabilityState.Full) ? AvailabilityState.Full : AvailabilityState.Unknown;
    }
    public Guid SubscriptionId { get; }
    public DateTimeOffset EvaluatedAtUtc { get; }
    public WatchStatus EffectiveWatchStatus { get; }
    public ImmutableArray<SectionHealthSnapshot> Members { get; }
    public int MemberCount => Members.Length;
    /// <summary>Members with current, verified known availability; failed/unchecked/stale members are uncovered.</summary>
    public int CoverageCount { get; }
    public bool CoverageComplete => CoverageCount == MemberCount;
    public AvailabilityState Availability { get; }
}
