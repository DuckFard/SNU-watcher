using System.Collections.Immutable;
using SnuWatcher.Application.Persistence;

namespace SnuWatcher.Application.Monitoring.Health;

public static class GroupHealthProjector
{
    public static GroupHealthSnapshot Project(SubscriptionSnapshot subscription, IEnumerable<SectionSnapshot> sections,
        DateTimeOffset now, TimeSpan requestedTarget)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        _ = SectionHealthProjector.Threshold(requestedTarget);
        var snapshot = PersistenceValidation.Snapshot(sections, PersistenceLimits.MaxSections, nameof(sections));
        if (snapshot.DistinctBy(s => s.Key).Count() != snapshot.Length)
            throw new ArgumentException("Section health states must have unique full identities.", nameof(sections));
        var members = subscription.Members.Select(m => m.Key).ToHashSet();
        if (snapshot.Any(s => !members.Contains(s.Key)))
            throw new ArgumentException("Section health must belong to the current subscription membership.", nameof(sections));
        now = now.ToUniversalTime();
        var states = snapshot.ToDictionary(s => s.Key);
        var projected = subscription.Members.Select(m => SectionHealthProjector.Project(
            states.GetValueOrDefault(m.Key) ?? SectionSnapshot.Unchecked(m.Key), now, requestedTarget)).ToImmutableArray();
        var effective = subscription.Status == WatchStatus.Removed ? WatchStatus.Removed
            : subscription.Expiry?.IsExpiredAt(now) == true ? WatchStatus.Expired : subscription.Status;
        return new(subscription.Id, effective, projected, now);
    }
}
