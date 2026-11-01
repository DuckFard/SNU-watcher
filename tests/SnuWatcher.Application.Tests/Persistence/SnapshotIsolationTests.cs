using System.Collections.Immutable;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Persistence;

public sealed class SnapshotIsolationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 5, 0, 0, TimeSpan.Zero);
    private static readonly SectionKey Key = new(2026, "2", "U000200002", "F32.102", "012", "000");

    private static SubscriptionSnapshot Subscription(Guid id, IEnumerable<MemberSnapshot> members) =>
        new(id, 1, 1, SubscriptionKind.Group, "Any statistics", WatchStatus.Active, true, null, Now, members);

    [Fact]
    public void ContextAndMembershipCannotBeChangedThroughInputOrReturnedLists()
    {
        var memberInput = new List<MemberSnapshot> { new(Key, 0, null) };
        var subscription = Subscription(Guid.NewGuid(), memberInput);
        memberInput.Clear();
        var subscriptions = new List<SubscriptionSnapshot> { subscription };
        var context = new CommitContext(new IssuedCheck(new(Guid.NewGuid(), 1), Now, [Key]), SectionSnapshot.Unchecked(Key), 1, subscriptions);
        subscriptions.Clear();

        Assert.Single(context.Subscriptions);
        Assert.Single(subscription.Members);
        Assert.Throws<NotSupportedException>(() => ((IList<MemberSnapshot>)subscription.Members).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<SubscriptionSnapshot>)context.Subscriptions).Clear());
    }

    [Fact]
    public void PreparedEffectsSnapshotInitializationAndKeepEvaluationTimeSeparateFromObservation()
    {
        var token = new CheckAttemptToken(Guid.NewGuid(), 1);
        var observation = SeatObservation.Valid(Key, 1, Now, new(119, 120), QuotaMeaning.EnrolledOverQuota);
        var completion = new SectionCompletion(token, observation, CheckOutcome.Success);
        var init = new List<MemberInitializationEffect> { new(Guid.NewGuid(), 1) };
        var effects = new SectionEffects(EvaluationOutcome.Opening, CheckOutcome.Success,
            new(Key, new(119, 120), QuotaMeaning.EnrolledOverQuota, 1, Now), init, null);
        var command = new PreparedSectionCommit(completion, 0, 1, effects, Now.AddMinutes(1));
        init.Clear();

        Assert.Single(command.Effects.Initializations);
        Assert.Equal(Now, command.Completion.ObservedAtUtc);
        Assert.Equal(Now.AddMinutes(1), command.EvaluatedAtUtc);
        Assert.Equal(0, command.ExpectedSectionVersion);
        Assert.Equal(1, command.ExpectedCatalogRevision);
    }

    [Fact]
    public void DeliveryRetainsStableDedupeAndTriggerFactsWhileCurrentCountsChange()
    {
        var watch = Subscription(Guid.NewGuid(), [new(Key, 0, 1)]);
        var trigger = new OpeningNotification(Key, 1, 0, 1, Now,
            [new(watch.Id.ToString("N"), 1, OpeningReason.IncreasedSeats)]);
        var eventId = Guid.NewGuid();
        var pending = new PendingDelivery(eventId, 1, trigger, DeliveryStatus.Pending, 0, Now);
        var current = new SectionSnapshot(Key, 2, 2, CheckOutcome.Success, Now.AddMinutes(1), Now.AddMinutes(1),
            new(Key, new(118, 120), QuotaMeaning.EnrolledOverQuota, 2, Now.AddMinutes(1)));
        var input = new List<SubscriptionSnapshot> { watch };
        var context = new DeliveryContext(pending, current, input);
        input.Clear();

        Assert.Equal(eventId.ToString("N"), pending.NativeDedupeKey);
        Assert.Equal(1, context.Delivery.Trigger.CurrentSeats);
        Assert.Equal(2, context.Section.Baseline!.Seats.ApparentSeats);
        Assert.Single(context.Subscriptions);
        var outcome = new DeliveryResultCommand(eventId, 1, DeliveryStatus.OsAccepted, 1, Now.AddMinutes(1));
        Assert.Equal(pending.EventId, outcome.EventId);
        Assert.Equal(DeliveryStatus.OsAccepted, outcome.Status);
        Assert.Equal(1, outcome.Attempts);
    }

    [Fact]
    public void SnapshotRejectsDuplicateMembersAndContextsOutsideIssuedScope()
    {
        Assert.Throws<ArgumentException>(() => Subscription(Guid.NewGuid(), [new(Key, 0, null), new(Key, 0, null)]));
        var other = new SectionKey(2026, "2", "U000200002", "F32.102", "013", "000");
        var issued = new IssuedCheck(new(Guid.NewGuid(), 1), Now, [Key]);
        Assert.Throws<ArgumentException>(() => new CommitContext(issued, SectionSnapshot.Unchecked(other), 1, []));
        var subscription = Subscription(Guid.NewGuid(), [new(other, 0, null)]);
        Assert.Throws<ArgumentException>(() => new CommitContext(issued, SectionSnapshot.Unchecked(Key), 1, [subscription]));
        var correct = Subscription(Guid.NewGuid(), [new(Key, 0, null)]);
        Assert.Throws<ArgumentException>(() => new CommitContext(issued, SectionSnapshot.Unchecked(Key), 1, [correct, correct]));
    }

    [Fact]
    public void EffectsRejectRecipientsOrInitializationForRejectedObservations()
    {
        var init = new MemberInitializationEffect(Guid.NewGuid(), 1);
        Assert.Throws<ArgumentException>(() => new SectionEffects(EvaluationOutcome.IgnoredUnknown, CheckOutcome.Unavailable, null, [init], null));
        var baseline = new SeatBaseline(Key, new(119, 120), QuotaMeaning.EnrolledOverQuota, 1, Now);
        Assert.Throws<ArgumentException>(() => new SectionEffects(EvaluationOutcome.NoOpening, CheckOutcome.Success, null, [], null));
        Assert.Throws<ArgumentException>(() => new SectionEffects(EvaluationOutcome.IgnoredUnknown, CheckOutcome.Unavailable, baseline, [], null));
        Assert.Throws<ArgumentException>(() => new SectionEffects(EvaluationOutcome.NoOpening, CheckOutcome.Success, baseline, [init, init], null));
        Assert.Throws<ArgumentException>(() => new SectionEffects(EvaluationOutcome.NoOpening, CheckOutcome.Unavailable, baseline, [], null));
    }

    [Fact]
    public void UnknownHealthCannotClaimASuccessWithoutValidState()
    {
        var unknown = new SectionSnapshot(Key, 1, 1, CheckOutcome.NotFound, Now, null, null);
        Assert.Null(unknown.Baseline);
        Assert.Null(unknown.LastSuccessfulAtUtc);
        Assert.Equal(CheckOutcome.NotFound, unknown.LatestOutcome);
        Assert.Throws<ArgumentException>(() => new SectionSnapshot(Key, 1, 1, null, Now, null, null));
        Assert.Throws<ArgumentException>(() => new SectionSnapshot(Key, 1, 1, CheckOutcome.NotFound, null, null, null));
        Assert.Throws<ArgumentException>(() => new SectionSnapshot(Key, 1, 1, CheckOutcome.NotFound, Now, Now, null));
    }

    [Fact]
    public void CommandsNormalizeInstantsAndPreserveResolvedExpiry()
    {
        var local = Now.ToOffset(TimeSpan.FromHours(9));
        var issue = new CheckIssue(local, [Key]);
        Assert.Equal(TimeSpan.Zero, issue.IssuedAtUtc.Offset);
        var expiry = WatchExpiry.FromLocalDate(new DateOnly(2026, 11, 2));
        var create = new NewSubscription(Guid.NewGuid(), "통계학", SubscriptionKind.Section, [Key], false, expiry, local);
        var snapshot = new SubscriptionSnapshot(create.Id, 1, 1, create.Kind, create.Label, WatchStatus.Paused,
            create.InitialNoticeEnabled, create.Expiry, create.CreatedAtUtc, [new(Key, 0, null)]);
        Assert.Equal(expiry.ExclusiveUtc, snapshot.Expiry!.ExclusiveUtc);
        Assert.Equal(expiry.ZoneId, snapshot.Expiry.ZoneId);
        Assert.Equal(TimeSpan.Zero, snapshot.CreatedAtUtc.Offset);
        Assert.Equal(WatchStatus.Paused, snapshot.Status);
    }

    [Fact]
    public void MalformedEnumsIdsVersionsAndExpiryFailBeforePersistence()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NewSubscription(Guid.NewGuid(), "x", (SubscriptionKind)99, [Key], true, null, Now));
        Assert.Throws<ArgumentException>(() => new NewSubscription(Guid.NewGuid(), new string('x', 121), SubscriptionKind.Section, [Key], true, null, Now));
        Assert.Throws<ArgumentException>(() => new SetSubscriptionExpiry(Guid.NewGuid(), 1, Now, new(new(2026, 11, 2), " ", Now)));
        Assert.Throws<ArgumentException>(() => new SetSubscriptionExpiry(Guid.NewGuid(), 1, Now, new(new(2026, 11, 2), "Asia/Seoul", Now.ToOffset(TimeSpan.FromHours(9)))));
        Assert.Throws<ArgumentException>(() => new MemberInitializationEffect(Guid.Empty, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemberInitializationEffect(Guid.NewGuid(), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeliveryResultCommand(Guid.NewGuid(), 0, DeliveryStatus.Pending, 0, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeliveryResultCommand(Guid.NewGuid(), 1, DeliveryStatus.Pending, -1, Now));
    }
}
