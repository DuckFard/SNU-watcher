using SnuWatcher.Application.Delivery;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Delivery;

public sealed class DeliveryPlannerTests
{
    [Fact]
    public void RetryUsesCurrentFewerSeatsAndKeepsOriginalTriggerFactsDistinct()
    {
        var context = DeliveryFixture.Context();
        var result = DeliveryPlanner.Plan(context, DeliveryFixture.Now, DeliveryFixture.MaxAge);

        Assert.Equal(DeliveryDisposition.Send, result.Disposition);
        Assert.Equal("ready", result.ReasonCode);
        var envelope = Assert.IsType<DeliveryEnvelope>(result.Envelope);
        Assert.Equal(2, envelope.CurrentSeats);
        Assert.Equal(new SeatCount(118, 120), envelope.CurrentCounts);
        Assert.Equal(DeliveryFixture.Now.AddSeconds(-10), envelope.CurrentSuccessAtUtc);
        Assert.Equal(2, envelope.CurrentAttemptOrdinal);
        Assert.Equal(context.Delivery.EventId, envelope.EventId);
        Assert.Equal(context.Delivery.NativeDedupeKey, envelope.NativeDedupeKey);
        Assert.Equal(3, envelope.DeliveryRevision);
        Assert.Equal(context.Delivery.Key, envelope.Key);
        Assert.Same(context.Delivery.Trigger, envelope.Trigger);
        Assert.Equal(4, envelope.Trigger.CurrentSeats);
        Assert.Equal(0, envelope.Trigger.PreviousSeats);
        Assert.Equal(DeliveryFixture.Now.AddMinutes(-10), envelope.Trigger.ObservedAt);
        Assert.Equal(1, envelope.Trigger.AttemptSequence);
    }

    [Theory]
    [InlineData(120, 120)]
    [InlineData(121, 120)]
    [InlineData(0, 0)]
    public void LaterKnownFullSupersedesTheOpening(int registered, int capacity)
    {
        var result = DeliveryPlanner.Plan(DeliveryFixture.Context(state: DeliveryFixture.State(registered, capacity)),
            DeliveryFixture.Now, DeliveryFixture.MaxAge);

        Assert.Equal(DeliveryDisposition.Superseded, result.Disposition);
        Assert.Equal("section_full", result.ReasonCode);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public void AnAgedKnownFullResultStillClosesTheOldOpening()
    {
        var result = DeliveryPlanner.Plan(DeliveryFixture.Context(state: DeliveryFixture.State(120, 120,
            successfulAt: DeliveryFixture.Now.AddHours(-1))), DeliveryFixture.Now, DeliveryFixture.MaxAge);

        Assert.Equal(DeliveryDisposition.Superseded, result.Disposition);
        Assert.Null(result.Envelope);
    }

    [Theory]
    [InlineData(CheckOutcome.Unavailable)]
    [InlineData(CheckOutcome.RateLimited)]
    [InlineData(CheckOutcome.AccessRestricted)]
    [InlineData(CheckOutcome.ParseChanged)]
    [InlineData(CheckOutcome.NotFound)]
    [InlineData(CheckOutcome.Incomplete)]
    public void NewerFailedOrUnknownHealthCannotSendOldOpeningCounts(CheckOutcome outcome)
    {
        var result = DeliveryPlanner.Plan(DeliveryFixture.Context(state: DeliveryFixture.State(latestOutcome: outcome)),
            DeliveryFixture.Now, DeliveryFixture.MaxAge);

        Assert.Equal(DeliveryDisposition.NeedsFreshCheck, result.Disposition);
        Assert.Equal("latest_check_unavailable", result.ReasonCode);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public void UnknownHealthDoesNotTurnAnOldFullBaselineIntoACurrentFullResult()
    {
        var result = DeliveryPlanner.Plan(DeliveryFixture.Context(state: DeliveryFixture.State(120, 120,
            latestOutcome: CheckOutcome.Unavailable)), DeliveryFixture.Now, DeliveryFixture.MaxAge);

        Assert.Equal(DeliveryDisposition.NeedsFreshCheck, result.Disposition);
        Assert.Equal("latest_check_unavailable", result.ReasonCode);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public void MissingBaselineRequestsFreshDataInsteadOfAssumingFull()
    {
        var result = DeliveryPlanner.Plan(DeliveryFixture.Context(state: SectionSnapshot.Unchecked(DeliveryFixture.Key())),
            DeliveryFixture.Now, DeliveryFixture.MaxAge);

        Assert.Equal(DeliveryDisposition.NeedsFreshCheck, result.Disposition);
        Assert.Equal("missing_current_counts", result.ReasonCode);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public void CountsFromBeforeTheTriggerCannotValidateItsDelivery()
    {
        var result = DeliveryPlanner.Plan(DeliveryFixture.Context(pending: DeliveryFixture.Pending(triggerOrdinal: 3)),
            DeliveryFixture.Now, DeliveryFixture.MaxAge);

        Assert.Equal(DeliveryDisposition.NeedsFreshCheck, result.Disposition);
        Assert.Equal("source_before_trigger", result.ReasonCode);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public void StaleOpenCountsWaitForFreshData()
    {
        var result = DeliveryPlanner.Plan(DeliveryFixture.Context(state: DeliveryFixture.State(
            successfulAt: DeliveryFixture.Now - DeliveryFixture.MaxAge - TimeSpan.FromTicks(1))),
            DeliveryFixture.Now, DeliveryFixture.MaxAge);

        Assert.Equal(DeliveryDisposition.NeedsFreshCheck, result.Disposition);
        Assert.Equal("source_stale", result.ReasonCode);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public void CountsExactlyAtTheMaximumAgeAreFresh()
    {
        var result = DeliveryPlanner.Plan(DeliveryFixture.Context(state: DeliveryFixture.State(
            successfulAt: DeliveryFixture.Now - DeliveryFixture.MaxAge)), DeliveryFixture.Now, DeliveryFixture.MaxAge);

        Assert.Equal(DeliveryDisposition.Send, result.Disposition);
        Assert.NotNull(result.Envelope);
    }

    [Theory]
    [InlineData(118)]
    [InlineData(120)]
    public void FutureCurrentSourceTimeNeedsARefreshWithoutClaimingItIsStale(int registered)
    {
        var result = DeliveryPlanner.Plan(DeliveryFixture.Context(state: DeliveryFixture.State(registered,
            successfulAt: DeliveryFixture.Now.AddTicks(1))), DeliveryFixture.Now, DeliveryFixture.MaxAge);

        Assert.Equal(DeliveryDisposition.NeedsFreshCheck, result.Disposition);
        Assert.Equal("clock_changed", result.ReasonCode);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public void TheTriggeringFreshObservationCanValidateItsOwnInitialDelivery()
    {
        var observedAt = DeliveryFixture.Now.AddSeconds(-10);
        var result = DeliveryPlanner.Plan(DeliveryFixture.Context(
            DeliveryFixture.Pending(triggerAt: observedAt),
            DeliveryFixture.State(116, successfulAt: observedAt, baselineOrdinal: 1)),
            DeliveryFixture.Now, DeliveryFixture.MaxAge);

        Assert.Equal(DeliveryDisposition.Send, result.Disposition);
        Assert.Equal(4, result.Envelope!.CurrentSeats);
        Assert.Equal(result.Envelope.Trigger.AttemptSequence, result.Envelope.CurrentAttemptOrdinal);
    }

    [Fact]
    public void FreshnessUsesInstantsAcrossOffsetsAndEnvelopeTimesAreUtc()
    {
        var observedAt = DeliveryFixture.Now.AddSeconds(-10).ToOffset(TimeSpan.FromHours(9));
        var result = DeliveryPlanner.Plan(DeliveryFixture.Context(state: DeliveryFixture.State(successfulAt: observedAt)),
            DeliveryFixture.Now, DeliveryFixture.MaxAge);

        Assert.Equal(DeliveryDisposition.Send, result.Disposition);
        Assert.Equal(observedAt.ToUniversalTime(), result.Envelope!.CurrentSuccessAtUtc);
        Assert.Equal(TimeSpan.Zero, result.Envelope.CurrentSuccessAtUtc.Offset);
    }

    [Fact]
    public void AReversedWallClockBetweenTriggerAndCurrentCheckDoesNotInvalidateNewerAttemptOrder()
    {
        var pending = DeliveryFixture.Pending(triggerAt: DeliveryFixture.Now.AddHours(2));
        var result = DeliveryPlanner.Plan(DeliveryFixture.Context(pending), DeliveryFixture.Now, DeliveryFixture.MaxAge);

        Assert.Equal(DeliveryDisposition.Send, result.Disposition);
        Assert.Equal(pending.Trigger.ObservedAt, result.Envelope!.Trigger.ObservedAt);
        Assert.True(result.Envelope.CurrentAttemptOrdinal > pending.Trigger.AttemptSequence);
        Assert.Equal(DeliveryFixture.Now.AddSeconds(-10), result.Envelope.CurrentSuccessAtUtc);
    }

    [Theory]
    [InlineData(DeliveryStatus.OsAccepted)]
    [InlineData(DeliveryStatus.Failed)]
    [InlineData(DeliveryStatus.Superseded)]
    public void TerminalEntriesNeverStartAnotherSend(DeliveryStatus status)
    {
        var result = DeliveryPlanner.Plan(DeliveryFixture.Context(DeliveryFixture.Pending(status)),
            DeliveryFixture.Now, DeliveryFixture.MaxAge);

        Assert.Equal(DeliveryDisposition.Terminal, result.Disposition);
        Assert.Equal("terminal", result.ReasonCode);
        Assert.Null(result.Envelope);
    }
}
