using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Tests.Monitoring.Health;

internal static class HealthTestData
{
    internal static readonly DateTimeOffset Now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    internal static SectionKey Key(string section = "012") => new(2026, "U000200002", "U000300002", "F32.102", section, "0");
    internal static SectionSnapshot Success(SectionKey key, int registered = 119, int capacity = 120, DateTimeOffset? at = null,
        QuotaMeaning? meaning = null)
    {
        var observed = at ?? Now;
        return new(key, 1, 1, CheckOutcome.Success, observed, observed,
            new(key, new(registered, capacity), meaning ?? QuotaMeaning.EnrolledOverQuota, 1, observed));
    }
    internal static SectionSnapshot Failure(SectionKey key, CheckOutcome outcome = CheckOutcome.Unavailable,
        DateTimeOffset? successfulAt = null, DateTimeOffset? attemptedAt = null)
    {
        var at = successfulAt ?? Now.AddSeconds(-10);
        return new(key, 2, 2, outcome, attemptedAt ?? Now, at,
            new(key, new(119, 120), QuotaMeaning.EnrolledOverQuota, 1, at));
    }
    internal static SubscriptionSnapshot Watch(IEnumerable<SectionKey> keys, WatchStatus status = WatchStatus.Active, WatchExpiry? expiry = null) =>
        new(Guid.NewGuid(), 1, 1, SubscriptionKind.Group, "Any Statistics section", status, true, expiry,
            Now.AddDays(-1), keys.Select(key => new MemberSnapshot(key, 0, null)));
}
