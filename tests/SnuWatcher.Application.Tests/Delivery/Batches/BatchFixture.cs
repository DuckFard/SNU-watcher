using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Tests.Delivery.Batches;

internal static class BatchFixture
{
    internal static readonly DateTimeOffset Now = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
    internal static readonly Guid BatchId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid FirstId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    internal static readonly Guid SecondId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    internal static readonly Guid AttemptId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    internal static readonly Guid RequestId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    internal static readonly string Digest = new('a', 64);
    internal static readonly string FactsDigest = new('b', 64);

    internal static SectionKey Key(string section = "012") => new(2026, "U000200002", "U000300002", "F32.102", section, "000");
    internal static BatchDeliveryPolicy Policy() => new(TimeSpan.FromMinutes(2), 3);
    internal static BatchCoverageSummary Coverage(long count = 2, long min = 1, long max = 2) => new(count, Digest, min, max);
    internal static DeliveryBatchSnapshot Batch(DeliveryBatchStatus status = DeliveryBatchStatus.Open,
        bool complete = true, long cutoff = 2) => new(BatchId, Key(), 4, status, cutoff, complete ? cutoff : 1, complete, Now.AddHours(-1));
    internal static BatchRecipient Recipient(Guid? id = null, long generation = 1, IEnumerable<OpeningReason>? reasons = null) =>
        new(id ?? FirstId, generation, reasons ?? [OpeningReason.IncreasedSeats]);
    internal static SubscriptionSnapshot Watch(Guid? id = null, WatchStatus status = WatchStatus.Active,
        long generation = 1, WatchExpiry? expiry = null, SectionKey? key = null, string label = "Statistics") =>
        new(id ?? FirstId, 1, generation, SubscriptionKind.Section, label, status, true, expiry, Now.AddHours(-1),
            [new MemberSnapshot(key ?? Key(), 0, 1)]);
    internal static SectionSnapshot State(int registered = 118, CheckOutcome outcome = CheckOutcome.Success,
        DateTimeOffset? sourceAt = null, long ordinal = 3)
    {
        var at = sourceAt ?? Now.AddSeconds(-10);
        var baseline = new SeatBaseline(Key(), new SeatCount(registered, 120), QuotaMeaning.EnrolledOverQuota, ordinal, at);
        return new(Key(), 3, outcome == CheckOutcome.Success ? ordinal : ordinal + 1, outcome,
            outcome == CheckOutcome.Success ? at : Now, at, baseline);
    }
    internal static BatchDeliveryContext Context(DeliveryBatchSnapshot? batch = null, SectionSnapshot? section = null,
        BatchCoverageSummary? coverage = null, IEnumerable<BatchRecipient>? recipients = null,
        IEnumerable<SubscriptionSnapshot>? subscriptions = null, long pending = 2, long ineligible = 0, long exhausted = 0,
        DateTimeOffset? evaluatedAt = null, DateTimeOffset? validUntil = null, Guid? unresolved = null, bool overflow = false) =>
        new(batch ?? Batch(), section ?? State(), 7, Policy(), evaluatedAt ?? Now, validUntil, pending, ineligible,
            exhausted, coverage ?? Coverage(), FactsDigest, recipients ?? (overflow ? [] : [Recipient()]),
            subscriptions ?? (overflow ? [] : [Watch()]), unresolved, overflow);
    internal static BatchDeliveryEnvelope Envelope(IEnumerable<BatchRecipient>? recipients = null) =>
        new(BatchId, AttemptId, Key(), new SeatCount(118, 120), Now.AddSeconds(-10), 3, recipients ?? [Recipient()], Coverage());
    internal static BatchAttemptSnapshot Attempt() => new(AttemptId, RequestId, 1, Now, Policy(), Envelope());
}
