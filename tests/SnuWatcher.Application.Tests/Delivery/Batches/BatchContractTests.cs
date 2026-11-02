using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Delivery.Batches;

public sealed class BatchContractTests
{
    [Fact]
    public void InputMutationCannotChangeCanonicalReasonUnionContextOrFrozenEnvelope()
    {
        List<OpeningReason> reasons = [OpeningReason.IncreasedSeats, OpeningReason.InitialAvailability];
        var recipient = BatchFixture.Recipient(reasons: reasons);
        List<BatchRecipient> recipients = [recipient];
        List<SubscriptionSnapshot> watches = [BatchFixture.Watch()];
        var context = BatchFixture.Context(recipients: recipients, subscriptions: watches);
        var envelope = BatchFixture.Envelope(recipients);
        reasons.Clear();
        recipients.Clear();
        watches.Clear();

        Assert.Equal(new[] { OpeningReason.InitialAvailability, OpeningReason.IncreasedSeats }, recipient.Reasons);
        Assert.Single(context.EligibleRecipients);
        Assert.Single(context.Subscriptions);
        Assert.Single(envelope.EligibleRecipients);
        Assert.Equal(BatchFixture.BatchId.ToString("N"), envelope.NativeDedupeKey);
        Assert.Equal(2, envelope.CurrentSeats);
        Assert.Throws<NotSupportedException>(() => ((IList<OpeningReason>)recipient.Reasons).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<BatchRecipient>)envelope.EligibleRecipients).Clear());
    }

    [Fact]
    public void LongMembershipCountsDoNotRequireMaterializingEventReferences()
    {
        var count = (long)int.MaxValue + 10;
        var context = BatchFixture.Context(batch: BatchFixture.Batch(cutoff: count), section: BatchFixture.State(ordinal: count),
            coverage: BatchFixture.Coverage(count, 1, count), pending: count);

        var decision = BatchDeliveryPlanner.Plan(context, BatchFixture.Now);

        Assert.Equal(BatchPlanDisposition.Send, decision.Disposition);
        Assert.Equal(count, decision.Content!.Coverage.EventCount);
        Assert.Single(decision.Content.EligibleRecipients);
    }

    [Fact]
    public void DuplicateReasonFactsBecomeOneReasonWhileDistinctReasonsRemain()
    {
        var recipient = BatchFixture.Recipient(reasons: [OpeningReason.IncreasedSeats, OpeningReason.IncreasedSeats]);
        Assert.Equal(new[] { OpeningReason.IncreasedSeats }, recipient.Reasons);
        Assert.Throws<ArgumentException>(() => BatchFixture.Recipient(reasons: []));
        Assert.Throws<ArgumentOutOfRangeException>(() => BatchFixture.Recipient(reasons: [(OpeningReason)99]));
        Assert.Throws<ArgumentException>(() => BatchFixture.Context(recipients: [recipient, recipient]));
    }

    [Fact]
    public void CoverageRejectsAmbiguousRangesOrUnboundedDiagnostics()
    {
        Assert.Throws<ArgumentException>(() => new BatchCoverageSummary(0, BatchFixture.Digest, 1, 1));
        Assert.Throws<ArgumentException>(() => new BatchCoverageSummary(2, BatchFixture.Digest, 1, 1));
        Assert.Throws<ArgumentException>(() => new BatchCoverageSummary(1, BatchFixture.Digest, 1, 2));
        Assert.Throws<ArgumentException>(() => new BatchCoverageSummary(1, "raw secret", 1, 1));
        Assert.Throws<ArgumentException>(() => new BatchCoverageSummary(1, new string('A', 64), 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BatchCoverageSummary(-1, BatchFixture.Digest, 0, 0));
        Assert.Throws<ArgumentException>(() => BatchFixture.Context(pending: 3));
        Assert.Throws<ArgumentException>(() => BatchFixture.Context(exhausted: long.MaxValue));
    }

    [Fact]
    public void RecipientOverflowIsExplicitAndCannotCarryASilentlyTruncatedSendableUnion()
    {
        var oversized = Enumerable.Range(0, BatchDeliveryLimits.MaxRecipients + 1)
            .Select(_ => BatchFixture.Recipient(Guid.NewGuid())).ToArray();
        Assert.Throws<ArgumentException>(() => BatchFixture.Context(recipients: oversized));
        Assert.Throws<ArgumentException>(() => BatchFixture.Context(overflow: true, recipients: [BatchFixture.Recipient()]));
        Assert.Equal(BatchPlanDisposition.RecipientLimitReached,
            BatchDeliveryPlanner.Plan(BatchFixture.Context(overflow: true), BatchFixture.Now).Disposition);
    }

    [Fact]
    public void ScopeAndAggregateAuthorityMustIdentifyOneExactSection()
    {
        Assert.Throws<ArgumentException>(() => BatchFixture.Context(section: SectionSnapshot.Unchecked(BatchFixture.Key("013"))));
        Assert.Throws<ArgumentException>(() => BatchFixture.Context(subscriptions: [BatchFixture.Watch(), BatchFixture.Watch()]));
        Assert.Throws<ArgumentException>(() => BatchFixture.Context(coverage: BatchFixture.Coverage(1, 3, 3), pending: 1));
        Assert.Throws<ArgumentException>(() => BatchFixture.Context(recipients: []));
        Assert.Throws<ArgumentException>(() => BatchFixture.Context(batch: BatchFixture.Batch(DeliveryBatchStatus.Closed), unresolved: BatchFixture.AttemptId));
    }

    [Fact]
    public void PreparedDecisionComparesSemanticFactsInsteadOfImmutableArrayIdentity()
    {
        var first = BatchFixture.Context();
        var equalCopy = BatchFixture.Context();
        var decision = BatchDeliveryPlanner.Plan(first, BatchFixture.Now);
        var prepared = new PreparedBatchAttempt(BatchFixture.RequestId, equalCopy, decision, BatchFixture.Now);

        Assert.Equal(BatchFixture.RequestId, prepared.ReservationRequestId);
        Assert.Equal(4, prepared.Context.Batch.Revision);
        Assert.Equal(7, prepared.Context.CatalogRevision);
        Assert.Equal(BatchFixture.Now, prepared.EvaluatedAtUtc);
        Assert.Throws<ArgumentException>(() => new PreparedBatchAttempt(BatchFixture.RequestId,
            BatchFixture.Context(section: BatchFixture.State(117)), decision, BatchFixture.Now));
    }

    [Fact]
    public void AttemptSnapshotRetainsFrozenPolicyAndCoverageAndUsesBatchNativeKey()
    {
        var attempt = BatchFixture.Attempt();
        Assert.Equal(BatchFixture.AttemptId, attempt.AttemptId);
        Assert.Equal(BatchFixture.RequestId, attempt.ReservationRequestId);
        Assert.Equal(1, attempt.AttemptSequence);
        Assert.Equal(BatchFixture.Now, attempt.PreparedAtUtc);
        Assert.Equal(3, attempt.Policy.MaxAttempts);
        Assert.Equal(BatchFixture.BatchId.ToString("N"), attempt.Envelope.NativeDedupeKey);
        Assert.Equal(2, attempt.Envelope.Coverage.EventCount);
        Assert.Throws<ArgumentException>(() => new BatchAttemptSnapshot(Guid.NewGuid(), BatchFixture.RequestId, 1,
            BatchFixture.Now, BatchFixture.Policy(), BatchFixture.Envelope()));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(86401, 3)]
    [InlineData(120, 0)]
    [InlineData(120, 101)]
    public void FrozenPolicyMustHaveBoundedFreshnessAndAttemptBudget(int seconds, int attempts)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BatchDeliveryPolicy(TimeSpan.FromSeconds(seconds), attempts));
    }

    [Fact]
    public void ResultCommandHasOnlyAttemptAuthorityAndSanitizedOutcomeFacts()
    {
        var at = BatchFixture.Now.ToOffset(TimeSpan.FromHours(9));
        var command = new BatchAttemptResultCommand(BatchFixture.AttemptId,
            default, "process_interrupted", at);
        Assert.Equal(BatchFixture.AttemptId, command.AttemptId);
        Assert.Equal(BatchAttemptResultOutcome.InterruptedUnknown, command.Outcome);
        Assert.Equal("process_interrupted", command.DiagnosticCode);
        Assert.Equal(TimeSpan.Zero, command.RecordedAtUtc.Offset);
        Assert.Throws<ArgumentException>(() => new BatchAttemptResultCommand(Guid.Empty,
            BatchAttemptResultOutcome.NotSubmitted, "not_submitted", at));
        Assert.Throws<ArgumentException>(() => new BatchAttemptResultCommand(BatchFixture.AttemptId,
            BatchAttemptResultOutcome.RetryableFailure, "private transport exception", at));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BatchAttemptResultCommand(BatchFixture.AttemptId,
            (BatchAttemptResultOutcome)99, "invalid", at));
    }

    [Fact]
    public void ReceiptsDistinguishZeroCostNotSubmittedAndUnknownBudgetResolution()
    {
        var notSubmitted = new BatchAttemptReceipt(BatchFixture.AttemptId, BatchFixture.BatchId,
            BatchAttemptResultOutcome.NotSubmitted, 2, 0, 2, 0, BatchFixture.Now);
        var interrupted = new BatchAttemptReceipt(BatchFixture.AttemptId, BatchFixture.BatchId,
            BatchAttemptResultOutcome.InterruptedUnknown, 2, 0, 1, 1, BatchFixture.Now);
        var accepted = new BatchAttemptReceipt(BatchFixture.AttemptId, BatchFixture.BatchId,
            BatchAttemptResultOutcome.OsAccepted, 2, 2, 0, 0, BatchFixture.Now);
        Assert.Equal(2, notSubmitted.PendingCount);
        Assert.Equal(0, notSubmitted.FailedCount);
        Assert.Equal(1, interrupted.PendingCount);
        Assert.Equal(1, interrupted.FailedCount);
        Assert.Equal(2, accepted.AcceptedCount);
        Assert.Equal(2, accepted.CoveredEventCount);
        Assert.Equal(BatchFixture.BatchId, accepted.BatchId);
        Assert.Throws<ArgumentException>(() => new BatchAttemptReceipt(BatchFixture.AttemptId, BatchFixture.BatchId,
            BatchAttemptResultOutcome.NotSubmitted, 2, 0, 1, 1, BatchFixture.Now));
        Assert.Throws<ArgumentException>(() => new BatchAttemptReceipt(BatchFixture.AttemptId, BatchFixture.BatchId,
            BatchAttemptResultOutcome.OsAccepted, 2, 1, 1, 0, BatchFixture.Now));
    }

    [Fact]
    public void ProjectionAndReservationCannotMasqueradeUnresolvedAuthorityAsReplayed()
    {
        var batch = BatchFixture.Batch();
        var projection = new BatchProjection(BatchProjectionStatus.UnresolvedAttempt, batch, BatchFixture.AttemptId);
        var reserved = new BatchReservationResult(BatchReservationStatus.Reserved, batch, BatchFixture.Attempt(), null, "reserved");
        var replay = new BatchReservationResult(BatchReservationStatus.Replayed, batch, BatchFixture.Attempt(), null, "replayed");
        Assert.Equal(BatchFixture.AttemptId, projection.UnresolvedAttemptId);
        Assert.Equal(BatchFixture.AttemptId, reserved.Attempt!.AttemptId);
        Assert.Equal(reserved.Attempt.AttemptId, replay.Attempt!.AttemptId);
        Assert.Throws<ArgumentException>(() => new BatchProjection(BatchProjectionStatus.ReadyToEvaluate, batch, BatchFixture.AttemptId));
        Assert.Throws<ArgumentException>(() => new BatchReservationResult(BatchReservationStatus.Replayed, batch, null,
            BatchFixture.AttemptId, "invalid_replay"));
    }

    [Fact]
    public void ResolvedReservationReplayExposesOriginalReceiptSoAcceptanceCannotBeSubmittedAgain()
    {
        var receipt = new BatchAttemptReceipt(BatchFixture.AttemptId, BatchFixture.BatchId,
            BatchAttemptResultOutcome.OsAccepted, 2, 2, 0, 0, BatchFixture.Now);
        var replay = new BatchReservationResult(BatchReservationStatus.Replayed,
            BatchFixture.Batch(DeliveryBatchStatus.Closed), BatchFixture.Attempt(), null, "replayed", receipt);

        Assert.Same(receipt, replay.Resolution);
        Assert.Equal(BatchAttemptResultOutcome.OsAccepted, replay.Resolution!.Outcome);
        Assert.Throws<ArgumentException>(() => new BatchReservationResult(BatchReservationStatus.Reserved,
            BatchFixture.Batch(), BatchFixture.Attempt(), null, "reserved", receipt));
    }

    [Fact]
    public void ReservationAuthorityCannotBePairedWithAnotherFullSectionOrAnUnfinishedBatch()
    {
        var wrongSection = new BatchDeliveryEnvelope(BatchFixture.BatchId, BatchFixture.AttemptId, BatchFixture.Key("013"),
            new SeatCount(118, 120), BatchFixture.Now.AddSeconds(-10), 3, [BatchFixture.Recipient()], BatchFixture.Coverage());
        var attempt = new BatchAttemptSnapshot(BatchFixture.AttemptId, BatchFixture.RequestId, 1,
            BatchFixture.Now, BatchFixture.Policy(), wrongSection);
        Assert.Throws<ArgumentException>(() => new BatchReservationResult(BatchReservationStatus.Reserved,
            BatchFixture.Batch(), attempt, null, "reserved"));
        Assert.Throws<ArgumentException>(() => new BatchReservationResult(BatchReservationStatus.Reserved,
            BatchFixture.Batch(DeliveryBatchStatus.Closed), BatchFixture.Attempt(), null, "reserved"));
        Assert.Throws<ArgumentException>(() => new BatchReservationResult(BatchReservationStatus.Reserved,
            BatchFixture.Batch(complete: false), BatchFixture.Attempt(), null, "reserved"));
    }

    [Fact]
    public void WorkAndAuditPageItemsRetainOnlyBoundedRelationalInspectionFacts()
    {
        var work = new SectionDeliveryWork(BatchFixture.Key(), 1, BatchFixture.BatchId, true);
        var project = new ProjectDeliveryBatchCommand(work.Key, 20, BatchFixture.Now);
        var item = new BatchAttemptCoverageItem(Guid.NewGuid(), 7, 3, 2);
        Assert.Equal(1, work.OldestPendingOrdinal);
        Assert.True(work.HasUnresolvedAttempt);
        Assert.Equal(20, project.PageSize);
        Assert.Equal(BatchFixture.Now, project.ProjectedAtUtc);
        Assert.Equal(7, item.TriggerOrdinal);
        Assert.Equal(3, item.OutboxRevisionAtSeal);
        Assert.Equal(2, item.AttemptsBefore);
        Assert.Throws<ArgumentException>(() => new SectionDeliveryWork(BatchFixture.Key(), 1, null, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectDeliveryBatchCommand(BatchFixture.Key(), 0, BatchFixture.Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectDeliveryBatchCommand(BatchFixture.Key(), BatchDeliveryLimits.MaxPageSize + 1, BatchFixture.Now));
    }

    [Fact]
    public void FoldProgressDistinguishesNoWorkFromIncompleteAndCompletedCutoff()
    {
        var noWork = new BatchProjection(BatchProjectionStatus.NoWork, null, null);
        var folding = new BatchProjection(BatchProjectionStatus.MoreToFold, BatchFixture.Batch(complete: false), null);
        var ready = new BatchProjection(BatchProjectionStatus.ReadyToEvaluate, BatchFixture.Batch(), null);
        Assert.Equal(BatchProjectionStatus.NoWork, noWork.Status);
        Assert.Null(noWork.Batch);
        Assert.Equal(1, folding.Batch!.FoldCursorOrdinal);
        Assert.Equal(2, folding.Batch.FoldCutoffOrdinal);
        Assert.False(folding.Batch.FoldComplete);
        Assert.Equal(ready.Batch!.FoldCursorOrdinal, ready.Batch.FoldCutoffOrdinal);
        Assert.True(ready.Batch.FoldComplete);
        Assert.Equal(BatchFixture.Now.AddHours(-1), ready.Batch.CreatedAtUtc);
        Assert.Throws<ArgumentException>(() => new DeliveryBatchSnapshot(BatchFixture.BatchId, BatchFixture.Key(), 1,
            DeliveryBatchStatus.Open, 2, 1, true, BatchFixture.Now));
        Assert.Throws<ArgumentException>(() => new DeliveryBatchSnapshot(BatchFixture.BatchId, BatchFixture.Key(), 1,
            DeliveryBatchStatus.Open, 2, 3, false, BatchFixture.Now));
        Assert.Throws<ArgumentException>(() => new BatchProjection(BatchProjectionStatus.MoreToFold, ready.Batch, null));
    }

    [Fact]
    public void FrozenEnvelopeCannotRepresentFullCountsMissingRecipientsOrAnUncoveredTrigger()
    {
        Assert.Throws<ArgumentException>(() => new BatchDeliveryEnvelope(BatchFixture.BatchId, BatchFixture.AttemptId,
            BatchFixture.Key(), new SeatCount(120, 120), BatchFixture.Now, 3, [BatchFixture.Recipient()], BatchFixture.Coverage()));
        Assert.Throws<ArgumentException>(() => new BatchDeliveryEnvelope(BatchFixture.BatchId, BatchFixture.AttemptId,
            BatchFixture.Key(), new SeatCount(118, 120), BatchFixture.Now, 3, [], BatchFixture.Coverage()));
        Assert.Throws<ArgumentException>(() => new BatchDeliveryEnvelope(BatchFixture.BatchId, BatchFixture.AttemptId,
            BatchFixture.Key(), new SeatCount(118, 120), BatchFixture.Now, 3, [BatchFixture.Recipient()], BatchFixture.Coverage(0, 0, 0)));
        Assert.Throws<ArgumentException>(() => new BatchDeliveryEnvelope(BatchFixture.BatchId, BatchFixture.AttemptId,
            BatchFixture.Key(), new SeatCount(118, 120), BatchFixture.Now, 1, [BatchFixture.Recipient()], BatchFixture.Coverage()));
    }

    [Fact]
    public void AlreadyCrossedEligibilityHintAndUninitializedReasonStorageFailClosed()
    {
        Assert.Throws<ArgumentException>(() => BatchFixture.Context(validUntil: BatchFixture.Now));
        Assert.Throws<ArgumentException>(() => BatchFixture.Recipient(reasons: default(System.Collections.Immutable.ImmutableArray<OpeningReason>)));
        Assert.Throws<ArgumentException>(() => BatchFixture.Recipient(reasons:
            [OpeningReason.InitialAvailability, OpeningReason.IncreasedSeats, OpeningReason.IncreasedSeats]));
    }
}
