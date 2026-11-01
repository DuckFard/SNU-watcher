using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Tests.Delivery;

internal static class DeliveryFixture
{
    internal static readonly DateTimeOffset Now = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
    internal static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);
    internal static readonly Guid EventId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    internal static readonly Guid FirstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid SecondId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    internal static SectionKey Key(string section = "012") => new(2026, "2", "U000200002", "F32.102", section, "000");

    internal static Recipient Recipient(Guid? id = null, long generation = 1) =>
        new((id ?? FirstId).ToString("N"), generation, OpeningReason.IncreasedSeats);

    internal static PendingDelivery Pending(DeliveryStatus status = DeliveryStatus.Pending,
        IEnumerable<Recipient>? recipients = null, DateTimeOffset? triggerAt = null, long triggerOrdinal = 1)
    {
        var at = triggerAt ?? Now.AddMinutes(-10);
        var trigger = new OpeningNotification(Key(), 4, 0, triggerOrdinal, at,
            (recipients ?? [Recipient()]).ToArray());
        return new(EventId, 3, trigger, status, 2, at);
    }

    internal static SectionSnapshot State(int registered = 118, int capacity = 120,
        DateTimeOffset? successfulAt = null, CheckOutcome latestOutcome = CheckOutcome.Success,
        long baselineOrdinal = 2, SectionKey? key = null)
    {
        var at = successfulAt ?? Now.AddSeconds(-10);
        var sectionKey = key ?? Key();
        var baseline = new SeatBaseline(sectionKey, new SeatCount(registered, capacity),
            QuotaMeaning.EnrolledOverQuota, baselineOrdinal, at);
        var latestOrdinal = latestOutcome == CheckOutcome.Success ? baselineOrdinal : baselineOrdinal + 1;
        var latestAt = latestOutcome == CheckOutcome.Success ? at : Now;
        return new(sectionKey, 2, latestOrdinal, latestOutcome, latestAt, at, baseline);
    }

    internal static SubscriptionSnapshot Watch(Guid? id = null, WatchStatus status = WatchStatus.Active,
        long generation = 1, WatchExpiry? expiry = null, SectionKey? key = null, string label = "Statistics",
        long revision = 1) =>
        new(id ?? FirstId, revision, generation, SubscriptionKind.Section, label, status, true, expiry,
            Now.AddHours(-1), [new MemberSnapshot(key ?? Key(), 0, 1)]);

    internal static DeliveryContext Context(PendingDelivery? pending = null, SectionSnapshot? state = null,
        IEnumerable<SubscriptionSnapshot>? subscriptions = null) =>
        new(pending ?? Pending(), state ?? State(), subscriptions ?? [Watch()]);
}
