using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Delivery.Batches;

public sealed class BatchDeliveryPlannerTests
{
    [Fact]
    public void IncreaseAndInitialRecipientsShareOneBoundedUnionWithBothOriginalReasons()
    {
        var first = BatchFixture.Recipient(reasons: [OpeningReason.IncreasedSeats, OpeningReason.InitialAvailability]);
        var second = BatchFixture.Recipient(BatchFixture.SecondId, reasons: [OpeningReason.InitialAvailability]);
        var context = BatchFixture.Context(recipients: [second, first],
            subscriptions: [BatchFixture.Watch(), BatchFixture.Watch(BatchFixture.SecondId)]);

        var decision = BatchDeliveryPlanner.Plan(context, BatchFixture.Now);

        Assert.Equal(BatchPlanDisposition.Send, decision.Disposition);
        Assert.Equal("ready", decision.ReasonCode);
        var content = Assert.IsType<BatchDeliveryContent>(decision.Content);
        Assert.Equal(new[] { BatchFixture.FirstId, BatchFixture.SecondId }, content.EligibleRecipients.Select(r => r.SubscriptionId));
        Assert.Equal(new[] { OpeningReason.InitialAvailability, OpeningReason.IncreasedSeats }, content.EligibleRecipients[0].Reasons);
        Assert.Equal(new[] { OpeningReason.InitialAvailability }, content.EligibleRecipients[1].Reasons);
        Assert.Equal(2, content.CurrentCounts.ApparentSeats);
        Assert.Equal(context.Section.Baseline!.ObservedAt, content.CurrentSuccessAtUtc);
        Assert.Equal(3, content.CurrentSourceOrdinal);
        Assert.Equal(2, content.Coverage.EventCount);
        Assert.Equal(BatchFixture.Digest, content.Coverage.Digest);
    }

    [Theory]
    [InlineData(CheckOutcome.Unavailable)]
    [InlineData(CheckOutcome.RateLimited)]
    [InlineData(CheckOutcome.AccessRestricted)]
    [InlineData(CheckOutcome.ParseChanged)]
    [InlineData(CheckOutcome.NotFound)]
    [InlineData(CheckOutcome.Incomplete)]
    public void NewUnknownCannotSendOrInferFullFromPreservedBaseline(CheckOutcome outcome)
    {
        foreach (var registered in new[] { 118, 120 })
        {
            var decision = BatchDeliveryPlanner.Plan(BatchFixture.Context(section: BatchFixture.State(registered, outcome)), BatchFixture.Now);
            Assert.Equal(BatchPlanDisposition.NeedsFreshCheck, decision.Disposition);
            Assert.Equal("latest_check_unavailable", decision.ReasonCode);
            Assert.Null(decision.Content);
        }
    }

    [Theory]
    [InlineData(120)]
    [InlineData(125)]
    public void KnownFullSupersedesOldOpeningEvenWhenCountsAreAged(int registered)
    {
        var context = BatchFixture.Context(section: BatchFixture.State(registered, sourceAt: BatchFixture.Now.AddDays(-1)));

        var decision = BatchDeliveryPlanner.Plan(context, BatchFixture.Now);

        Assert.Equal(BatchPlanDisposition.ResolveWithoutSend, decision.Disposition);
        Assert.Equal("section_full", decision.ReasonCode);
        Assert.Null(decision.Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrStaleCountsWaitForFreshCheck(bool missing)
    {
        var state = missing ? SectionSnapshot.Unchecked(BatchFixture.Key())
            : BatchFixture.State(sourceAt: BatchFixture.Now.AddMinutes(-2).AddTicks(-1));
        var decision = BatchDeliveryPlanner.Plan(BatchFixture.Context(section: state), BatchFixture.Now);
        Assert.Equal(BatchPlanDisposition.NeedsFreshCheck, decision.Disposition);
        Assert.Equal(missing ? "missing_current_counts" : "source_stale", decision.ReasonCode);
        Assert.Null(decision.Content);
    }

    [Fact]
    public void ExactFreshnessBoundaryIsSendableAndFutureSourceIsUnknown()
    {
        var boundary = BatchDeliveryPlanner.Plan(BatchFixture.Context(section:
            BatchFixture.State(sourceAt: BatchFixture.Now.AddMinutes(-2))), BatchFixture.Now);
        var future = BatchDeliveryPlanner.Plan(BatchFixture.Context(section:
            BatchFixture.State(sourceAt: BatchFixture.Now.AddTicks(1))), BatchFixture.Now);
        Assert.Equal(BatchPlanDisposition.Send, boundary.Disposition);
        Assert.Equal(BatchPlanDisposition.NeedsFreshCheck, future.Disposition);
        Assert.Equal("clock_changed", future.ReasonCode);
    }

    [Fact]
    public void BaselineMustCoverHighestEligibleTriggerRatherThanAllHistoricalMembers()
    {
        var tooOld = BatchDeliveryPlanner.Plan(BatchFixture.Context(section: BatchFixture.State(ordinal: 1)), BatchFixture.Now);
        var invalidLaterEvent = BatchFixture.Context(batch: BatchFixture.Batch(cutoff: 100),
            section: BatchFixture.State(ordinal: 2), coverage: BatchFixture.Coverage(1, 1, 1), pending: 2, ineligible: 1);

        Assert.Equal(BatchPlanDisposition.NeedsFreshCheck, tooOld.Disposition);
        Assert.Equal("source_before_trigger", tooOld.ReasonCode);
        Assert.Equal(BatchPlanDisposition.Send, BatchDeliveryPlanner.Plan(invalidLaterEvent, BatchFixture.Now).Disposition);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(0, 2)]
    [InlineData(1, 1)]
    public void NoEligibleEventsResolveInvalidAndBudgetMembersWithoutInferringSourceState(long invalid, long budget)
    {
        var context = BatchFixture.Context(section: SectionSnapshot.Unchecked(BatchFixture.Key()),
            coverage: BatchFixture.Coverage(0, 0, 0), recipients: [], subscriptions: [], ineligible: invalid, exhausted: budget);

        var decision = BatchDeliveryPlanner.Plan(context, BatchFixture.Now);

        Assert.Equal(BatchPlanDisposition.ResolveWithoutSend, decision.Disposition);
        Assert.Equal("no_eligible_events", decision.ReasonCode);
        Assert.Null(decision.Content);
        Assert.Equal(invalid, context.IneligibleCount);
        Assert.Equal(budget, context.BudgetExhaustedCount);
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("removed")]
    [InlineData("resumed")]
    [InlineData("missing")]
    [InlineData("member")]
    public void ChangedRecipientStateRequiresReaggregationRatherThanFilteringCounts(string change)
    {
        var watches = change switch
        {
            "paused" => new[] { BatchFixture.Watch(status: WatchStatus.Paused) },
            "removed" => [BatchFixture.Watch(status: WatchStatus.Removed)],
            "resumed" => [BatchFixture.Watch(generation: 2)],
            "missing" => [],
            _ => [BatchFixture.Watch(key: BatchFixture.Key("013"))],
        };

        var decision = BatchDeliveryPlanner.Plan(BatchFixture.Context(subscriptions: watches), BatchFixture.Now);

        Assert.Equal(BatchPlanDisposition.NeedsRevalidation, decision.Disposition);
        Assert.Equal("recipient_state_changed", decision.ReasonCode);
        Assert.Null(decision.Content);
    }

    [Fact]
    public void OneOverlapRecipientExpiringRequiresNewAggregateWithoutDroppingSharedEvent()
    {
        var expiry = WatchExpiry.FromLocalDate(new DateOnly(2026, 11, 1), TimeZoneInfo.Utc);
        var context = BatchFixture.Context(coverage: BatchFixture.Coverage(1, 1, 1), pending: 1,
            recipients: [BatchFixture.Recipient(), BatchFixture.Recipient(BatchFixture.SecondId)],
            subscriptions: [BatchFixture.Watch(expiry: expiry), BatchFixture.Watch(BatchFixture.SecondId)],
            evaluatedAt: BatchFixture.Now.AddSeconds(-1), validUntil: expiry.ExclusiveUtc);

        var atExpiry = BatchDeliveryPlanner.Plan(context, BatchFixture.Now);
        var refreshed = BatchFixture.Context(coverage: BatchFixture.Coverage(1, 1, 1), pending: 1,
            recipients: [BatchFixture.Recipient(BatchFixture.SecondId)], subscriptions: [BatchFixture.Watch(BatchFixture.SecondId)]);

        Assert.Equal(BatchPlanDisposition.NeedsRevalidation, atExpiry.Disposition);
        Assert.Equal("eligibility_expired", atExpiry.ReasonCode);
        Assert.Null(atExpiry.Content);
        Assert.Equal(1, BatchDeliveryPlanner.Plan(refreshed, BatchFixture.Now).Content!.Coverage.EventCount);
    }

    [Fact]
    public void ExpiryIsCheckedEvenWithoutAnOptimisticValidityHintAndRenameRemainsEligible()
    {
        var expiry = WatchExpiry.FromLocalDate(new DateOnly(2026, 11, 1), TimeZoneInfo.Utc);
        var expired = BatchFixture.Context(subscriptions: [BatchFixture.Watch(expiry: expiry)], evaluatedAt: BatchFixture.Now.AddSeconds(-1));
        var renamed = BatchFixture.Context(subscriptions: [BatchFixture.Watch(label: "Renamed")]);
        Assert.Equal(BatchPlanDisposition.NeedsRevalidation, BatchDeliveryPlanner.Plan(expired, BatchFixture.Now).Disposition);
        Assert.Equal(BatchPlanDisposition.Send, BatchDeliveryPlanner.Plan(renamed, BatchFixture.Now).Disposition);
    }

    [Fact]
    public void ClockRollbackBeforeAggregationTimeRequiresReread()
    {
        var decision = BatchDeliveryPlanner.Plan(BatchFixture.Context(), BatchFixture.Now.AddTicks(-1));
        Assert.Equal(BatchPlanDisposition.NeedsRevalidation, decision.Disposition);
        Assert.Equal("clock_changed", decision.ReasonCode);
    }

    [Theory]
    [InlineData("fold", BatchPlanDisposition.MoreToFold)]
    [InlineData("unresolved", BatchPlanDisposition.UnresolvedAttempt)]
    [InlineData("closed", BatchPlanDisposition.Closed)]
    [InlineData("overflow", BatchPlanDisposition.RecipientLimitReached)]
    public void UnfinishedOrClosedAuthorityNeverProducesSendableContent(string state, BatchPlanDisposition expected)
    {
        var context = state switch
        {
            "fold" => BatchFixture.Context(batch: BatchFixture.Batch(complete: false)),
            "unresolved" => BatchFixture.Context(unresolved: BatchFixture.AttemptId),
            "closed" => BatchFixture.Context(batch: BatchFixture.Batch(DeliveryBatchStatus.Closed),
                coverage: BatchFixture.Coverage(0, 0, 0), recipients: [], subscriptions: [], pending: 0),
            _ => BatchFixture.Context(overflow: true),
        };

        var decision = BatchDeliveryPlanner.Plan(context, BatchFixture.Now);

        Assert.Equal(expected, decision.Disposition);
        Assert.Null(decision.Content);
    }

    [Fact]
    public void NullContextIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => BatchDeliveryPlanner.Plan(null!, BatchFixture.Now));
    }
}
