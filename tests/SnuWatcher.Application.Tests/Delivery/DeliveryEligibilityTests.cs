using SnuWatcher.Application.Delivery;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Delivery;

public sealed class DeliveryEligibilityTests
{
    [Theory]
    [InlineData(WatchStatus.Paused)]
    [InlineData(WatchStatus.Expired)]
    [InlineData(WatchStatus.Removed)]
    public void InactiveRecipientsSupersedeAnOtherwiseFreshOpening(WatchStatus status)
    {
        var result = DeliveryPlanner.Plan(DeliveryFixture.Context(subscriptions: [DeliveryFixture.Watch(status: status)]),
            DeliveryFixture.Now, DeliveryFixture.MaxAge);

        Assert.Equal(DeliveryDisposition.Superseded, result.Disposition);
        Assert.Equal("no_eligible_recipients", result.ReasonCode);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public void NoRemainingRecipientSupersedesEvenWhenSourceDataIsUnavailable()
    {
        var result = DeliveryPlanner.Plan(DeliveryFixture.Context(state: SectionSnapshot.Unchecked(DeliveryFixture.Key()),
            subscriptions: []), DeliveryFixture.Now, DeliveryFixture.MaxAge);

        Assert.Equal(DeliveryDisposition.Superseded, result.Disposition);
        Assert.Equal("no_eligible_recipients", result.ReasonCode);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public void AResumedGenerationCannotReceiveItsPreviousGenerationsPendingAlert()
    {
        var result = DeliveryPlanner.Plan(DeliveryFixture.Context(subscriptions: [DeliveryFixture.Watch(generation: 2)]),
            DeliveryFixture.Now, DeliveryFixture.MaxAge);

        Assert.Equal(DeliveryDisposition.Superseded, result.Disposition);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public void CurrentMembershipMustStillContainTheExactSection()
    {
        var result = DeliveryPlanner.Plan(DeliveryFixture.Context(subscriptions:
            [DeliveryFixture.Watch(key: DeliveryFixture.Key("013"))]), DeliveryFixture.Now, DeliveryFixture.MaxAge);

        Assert.Equal(DeliveryDisposition.Superseded, result.Disposition);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public void AGroupCanReceiveAnAlertForAnyOfItsConfirmedMembers()
    {
        var watch = new SubscriptionSnapshot(DeliveryFixture.FirstId, 1, 1, SubscriptionKind.Group, "Any statistics",
            WatchStatus.Active, true, null, DeliveryFixture.Now.AddHours(-1),
            [new MemberSnapshot(DeliveryFixture.Key("013"), 0, 1), new MemberSnapshot(DeliveryFixture.Key(), 0, 1)]);
        var result = DeliveryPlanner.Plan(DeliveryFixture.Context(subscriptions: [watch]),
            DeliveryFixture.Now, DeliveryFixture.MaxAge);

        Assert.Equal(DeliveryDisposition.Send, result.Disposition);
        Assert.Equal(DeliveryFixture.FirstId.ToString("N"), Assert.Single(result.Envelope!.EligibleRecipients).SubscriptionId);
    }

    [Fact]
    public void ExpiryAtDeliveryTimeExcludesOnlyTheExpiredRecipient()
    {
        var expiry = WatchExpiry.FromLocalDate(new DateOnly(2026, 11, 1), TimeZoneInfo.Utc);
        var pending = DeliveryFixture.Pending(recipients: [DeliveryFixture.Recipient(), DeliveryFixture.Recipient(DeliveryFixture.SecondId)]);
        var context = DeliveryFixture.Context(pending, subscriptions:
            [DeliveryFixture.Watch(expiry: expiry), DeliveryFixture.Watch(DeliveryFixture.SecondId)]);
        var result = DeliveryPlanner.Plan(context, expiry.ExclusiveUtc, DeliveryFixture.MaxAge);

        Assert.Equal(DeliveryDisposition.Send, result.Disposition);
        Assert.Equal(DeliveryFixture.SecondId.ToString("N"), Assert.Single(result.Envelope!.EligibleRecipients).SubscriptionId);
        Assert.Equal(2, context.Delivery.Trigger.Recipients.Count);
    }

    [Fact]
    public void RenameAndExpiryExtensionPreserveAnEligiblePendingAlert()
    {
        var watch = DeliveryFixture.Watch(label: "Renamed group", revision: 7,
            expiry: WatchExpiry.FromLocalDate(new DateOnly(2026, 11, 3), TimeZoneInfo.Utc));
        var result = DeliveryPlanner.Plan(DeliveryFixture.Context(subscriptions: [watch]),
            DeliveryFixture.Now, DeliveryFixture.MaxAge);

        Assert.Equal(DeliveryDisposition.Send, result.Disposition);
        Assert.Equal(DeliveryFixture.FirstId.ToString("N"), Assert.Single(result.Envelope!.EligibleRecipients).SubscriptionId);
    }

    [Fact]
    public void NewlyAddedSubscriptionsAreNotAddedToAnOlderEventsRecipients()
    {
        var result = DeliveryPlanner.Plan(DeliveryFixture.Context(subscriptions:
            [DeliveryFixture.Watch(), DeliveryFixture.Watch(DeliveryFixture.SecondId)]),
            DeliveryFixture.Now, DeliveryFixture.MaxAge);

        Assert.Equal(DeliveryDisposition.Send, result.Disposition);
        Assert.Equal(DeliveryFixture.FirstId.ToString("N"), Assert.Single(result.Envelope!.EligibleRecipients).SubscriptionId);
    }

    [Fact]
    public void ReplanningKeepsTheSameNativeDedupeKeyAndDeliveryRevision()
    {
        var context = DeliveryFixture.Context();
        var first = DeliveryPlanner.Plan(context, DeliveryFixture.Now, DeliveryFixture.MaxAge).Envelope!;
        var retry = DeliveryPlanner.Plan(context, DeliveryFixture.Now.AddSeconds(1), DeliveryFixture.MaxAge).Envelope!;

        Assert.Equal(first.EventId, retry.EventId);
        Assert.Equal(first.NativeDedupeKey, retry.NativeDedupeKey);
        Assert.Equal(DeliveryFixture.EventId.ToString("N"), retry.NativeDedupeKey);
        Assert.Equal(first.DeliveryRevision, retry.DeliveryRevision);
    }

    [Fact]
    public void EnvelopeAndContextCollectionsCannotBeChangedThroughMutableInputs()
    {
        var recipients = new List<Recipient> { DeliveryFixture.Recipient() };
        var subscriptions = new List<SubscriptionSnapshot> { DeliveryFixture.Watch() };
        var context = DeliveryFixture.Context(DeliveryFixture.Pending(recipients: recipients), subscriptions: subscriptions);
        var result = DeliveryPlanner.Plan(context, DeliveryFixture.Now, DeliveryFixture.MaxAge);
        recipients.Clear();
        subscriptions.Clear();

        Assert.Single(result.Envelope!.EligibleRecipients);
        Assert.Single(result.Envelope.Trigger.Recipients);
        var exposed = Assert.IsAssignableFrom<IList<Recipient>>(result.Envelope.EligibleRecipients);
        Assert.Throws<NotSupportedException>(() => exposed.Clear());
        Assert.Single(context.Subscriptions);
    }

    [Fact]
    public void DuplicateSubscriptionSnapshotsAreRejectedInsteadOfChoosingAnArbitraryRevision()
    {
        var context = DeliveryFixture.Context(subscriptions:
            [DeliveryFixture.Watch(), DeliveryFixture.Watch(status: WatchStatus.Paused, revision: 2)]);

        Assert.Throws<ArgumentException>(() => DeliveryPlanner.Plan(context, DeliveryFixture.Now, DeliveryFixture.MaxAge));
    }

    [Fact]
    public void AContextCannotUseAnotherSectionsCounts()
    {
        Assert.Throws<ArgumentException>(() => new DeliveryContext(DeliveryFixture.Pending(),
            DeliveryFixture.State(key: DeliveryFixture.Key("013")), [DeliveryFixture.Watch()]));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public void MaximumAgeMustBePositiveAndBounded(long ticks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DeliveryPlanner.Plan(DeliveryFixture.Context(),
            DeliveryFixture.Now, TimeSpan.FromTicks(ticks)));
    }

    [Fact]
    public void MaximumAgeMayBeOneDayButCannotExceedIt()
    {
        var result = DeliveryPlanner.Plan(DeliveryFixture.Context(), DeliveryFixture.Now, TimeSpan.FromDays(1));
        Assert.Equal(DeliveryDisposition.Send, result.Disposition);
        Assert.Throws<ArgumentOutOfRangeException>(() => DeliveryPlanner.Plan(DeliveryFixture.Context(),
            DeliveryFixture.Now, TimeSpan.FromDays(1) + TimeSpan.FromTicks(1)));
        Assert.Throws<ArgumentNullException>(() => DeliveryPlanner.Plan(null!, DeliveryFixture.Now, DeliveryFixture.MaxAge));
    }
}
