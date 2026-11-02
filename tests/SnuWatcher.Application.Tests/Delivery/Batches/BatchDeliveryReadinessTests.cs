using SnuWatcher.Application.Delivery.Batches;
using Xunit;

namespace SnuWatcher.Application.Tests.Delivery.Batches;

public sealed class BatchDeliveryReadinessTests
{
    [Theory]
    [InlineData(BatchChannelReadiness.Initializing, "channel_initializing")]
    [InlineData(BatchChannelReadiness.Disabled, "channel_disabled")]
    [InlineData(BatchChannelReadiness.Unavailable, "channel_unavailable")]
    [InlineData((BatchChannelReadiness)99, "channel_unavailable")]
    public async Task BlockedReadinessDoesNotReserveAnAttemptOrInventAnyResult(BatchChannelReadiness readiness, string reason)
    {
        using var f = new CoordinatorFixture();
        f.Channel.Readiness = _ => Task.FromResult(readiness);
        var result = await f.RunAsync(f.Coordinator());
        Assert.Equal(BatchDeliveryRunDisposition.ChannelDeferred, result.Disposition);
        Assert.Equal(reason, result.ReasonCode);
        Assert.False(result.SubmittedThisCall);
        Assert.Null(result.AttemptId);
        Assert.Null(result.KnownResult);
        Assert.Null(result.Receipt);
        Assert.Empty(f.Port.Reservations);
        Assert.Empty(f.Port.Results);
        Assert.Empty(f.Channel.Submissions);
    }

    [Theory]
    [InlineData(BatchNoSubmissionReason.Cancelled, "not_submitted_cancelled")]
    [InlineData(BatchNoSubmissionReason.ChannelNotReady, "not_submitted_channel_not_ready")]
    [InlineData(BatchNoSubmissionReason.ValidityChanged, "not_submitted_validity_changed")]
    [InlineData(BatchNoSubmissionReason.PayloadRejected, "not_submitted_payload_rejected")]
    public async Task ProvenNoCallAfterSealIsNotAChargedInvocation(BatchNoSubmissionReason reason, string diagnostic)
    {
        using var f = new CoordinatorFixture();
        f.Channel.SendResult = (_, _) => Task.FromResult(new BatchChannelResult(BatchChannelOutcome.NotSubmitted, reason));
        var result = await f.RunAsync(f.Coordinator());
        Assert.Equal(BatchDeliveryRunDisposition.ResultRecorded, result.Disposition);
        Assert.Equal(BatchChannelOutcome.NotSubmitted, result.ChannelOutcome);
        Assert.False(result.SubmittedThisCall);
        Assert.Equal(BatchAttemptResultOutcome.NotSubmitted, result.KnownResult!.Outcome);
        Assert.Equal(diagnostic, result.KnownResult.DiagnosticCode);
        Assert.Equal(2, result.Receipt!.PendingCount);
    }

    [Fact]
    public async Task ReadinessExceptionsAreSanitizedBeforeReservationButRequestedCancellationPropagates()
    {
        using var f = new CoordinatorFixture();
        f.Channel.Readiness = _ => throw new InvalidOperationException("private native details");
        var result = await f.RunAsync(f.Coordinator());
        Assert.Equal("channel_unavailable", result.ReasonCode);
        Assert.Empty(f.Port.Reservations);
        using var cancellation = new CancellationTokenSource();
        f.Channel.Readiness = token => { cancellation.Cancel(); return Task.FromCanceled<BatchChannelReadiness>(token); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.RunAsync(f.Coordinator(), cancellation.Token));
        Assert.Empty(f.Port.Results);
    }

    [Fact]
    public async Task ProbeWaitCrossingSourceAgeReplansBeforeCreatingAnyAuthority()
    {
        using var f = new CoordinatorFixture();
        f.Channel.Readiness = _ => { f.Clock.Now += TimeSpan.FromMinutes(3); return Task.FromResult(BatchChannelReadiness.Ready); };
        var result = await f.RunAsync(f.Coordinator());
        Assert.Equal(BatchDeliveryRunDisposition.NeedsFreshCheck, result.Disposition);
        Assert.Empty(f.Port.Reservations);
        Assert.Empty(f.Channel.Submissions);
    }

    [Fact]
    public async Task ResultFlushBypassesReadinessAndPreservesExactNoSubmissionCommand()
    {
        using var f = new CoordinatorFixture();
        f.Channel.SendResult = (_, _) => Task.FromResult(new BatchChannelResult(BatchChannelOutcome.NotSubmitted, BatchNoSubmissionReason.ChannelNotReady));
        f.Port.Record = (_, _) => throw new IOException("private result details");
        var coordinator = f.Coordinator();
        var first = await f.RunAsync(coordinator);
        var probes = f.Channel.ReadinessReads;
        f.Channel.Readiness = _ => throw new InvalidOperationException("flush must not inspect native state");
        f.Port.Record = (command, _) => Task.FromResult(BatchPort.Receipt(command));
        var flushed = await f.RunAsync(coordinator);
        Assert.Same(first.KnownResult, flushed.KnownResult);
        Assert.Equal(probes, f.Channel.ReadinessReads);
        Assert.False(flushed.SubmittedThisCall);
        Assert.Single(f.Channel.Submissions);
    }

    [Fact]
    public async Task ValidityWindowUsesEarliestRecipientExpiryAndSealedPolicyWithoutMutatingEnvelope()
    {
        using var f = new CoordinatorFixture();
        var expiry = BatchFixture.Now.AddSeconds(10);
        f.Port.Read = (at, _) => Task.FromResult(BatchFixture.Context(evaluatedAt: at, validUntil: expiry));
        await f.RunAsync(f.Coordinator());
        var submission = Assert.Single(f.Channel.Submissions);
        Assert.Same(Assert.Single(f.Channel.Envelopes), submission.Envelope);
        Assert.Equal(expiry, submission.EligibilityExpiresAtUtc);
        Assert.Equal(BatchFixture.Policy().MaxAge, submission.MaxSourceAge);
        Assert.Equal(BatchFixture.Now, submission.ValidatedAtUtc);
    }

    [Fact]
    public async Task LogicalFullResolutionNeverNeedsNativeReadiness()
    {
        using var f = new CoordinatorFixture();
        f.Port.Read = (at, _) => Task.FromResult(BatchFixture.Context(evaluatedAt: at, section: BatchFixture.State(registered: 120)));
        f.Channel.Readiness = _ => throw new InvalidOperationException("logical closure must not inspect Windows");
        var result = await f.RunAsync(f.Coordinator());
        Assert.Equal(BatchDeliveryRunDisposition.Closed, result.Disposition);
        Assert.Equal(0, f.Channel.ReadinessReads);
        Assert.Empty(f.Channel.Submissions);
    }

    [Fact]
    public async Task ClockRollbackBelowActualSealIsProvenNoSubmissionEvenIfBaselineStillLooksFresh()
    {
        using var f = new CoordinatorFixture();
        var reserve = f.Port.Reserve;
        f.Port.Reserve = async (command, token) =>
        {
            var result = await reserve(command, token);
            var original = result.Attempt!;
            var sealedAfterCurrentWallClock = new BatchAttemptSnapshot(original.AttemptId, original.ReservationRequestId,
                original.AttemptSequence, f.Clock.Now.AddSeconds(30), original.Policy, original.Envelope);
            return new BatchReservationResult(result.Status, result.Batch, sealedAfterCurrentWallClock, null, result.ReasonCode);
        };
        var result = await f.RunAsync(f.Coordinator());
        Assert.Equal(BatchAttemptResultOutcome.NotSubmitted, result.KnownResult!.Outcome);
        Assert.Equal("not_submitted_clock_changed", result.KnownResult.DiagnosticCode);
        Assert.False(result.SubmittedThisCall);
        Assert.Empty(f.Channel.Submissions);
    }

    [Fact]
    public async Task CancelledCallerAfterSuccessfulProbeCannotSealOrCallAdapter()
    {
        using var f = new CoordinatorFixture();
        using var cancellation = new CancellationTokenSource();
        f.Channel.Readiness = _ => { cancellation.Cancel(); return Task.FromResult(BatchChannelReadiness.Ready); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.RunAsync(f.Coordinator(), cancellation.Token));
        Assert.Empty(f.Port.Reservations);
        Assert.Empty(f.Channel.Submissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullOrExplicitUnknownResultCannotAcknowledgeAnyOpening(bool explicitUnknown)
    {
        using var f = new CoordinatorFixture();
        f.Channel.SendResult = (_, _) => Task.FromResult(explicitUnknown
            ? new BatchChannelResult(BatchChannelOutcome.AcceptanceUnknown) : null!);
        var result = await f.RunAsync(f.Coordinator());
        Assert.Equal(BatchChannelOutcome.AcceptanceUnknown, result.ChannelOutcome);
        Assert.Equal(BatchAttemptResultOutcome.InterruptedUnknown, result.KnownResult!.Outcome);
        Assert.True(result.SubmittedThisCall);
        Assert.Equal(0, result.Receipt!.AcceptedCount);
    }

    [Fact]
    public async Task LateCallerCancellationCannotEraseProvenNoSubmissionOrCancelItsResultWrite()
    {
        using var f = new CoordinatorFixture();
        using var cancellation = new CancellationTokenSource();
        f.Channel.SendResult = (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new BatchChannelResult(BatchChannelOutcome.NotSubmitted, BatchNoSubmissionReason.Cancelled));
        };
        f.Port.Record = (command, token) =>
        {
            Assert.False(token.CanBeCanceled);
            return Task.FromResult(BatchPort.Receipt(command));
        };
        var result = await f.RunAsync(f.Coordinator(), cancellation.Token);
        Assert.Equal(BatchAttemptResultOutcome.NotSubmitted, result.Receipt!.Outcome);
        Assert.False(result.SubmittedThisCall);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignEnvelopeAuthorityCannotBeSubmittedOrUsedForAResultWrite(bool foreignKey)
    {
        using var f = new CoordinatorFixture();
        f.Port.Reserve = (command, _) =>
        {
            var original = BatchFixture.Envelope();
            var envelope = new BatchDeliveryEnvelope(foreignKey ? original.BatchId : Guid.NewGuid(), original.AttemptId,
                foreignKey ? BatchFixture.Key("013") : original.Key, original.CurrentCounts, original.CurrentSuccessAtUtc,
                original.CurrentSourceOrdinal, original.EligibleRecipients, original.Coverage);
            var attempt = new BatchAttemptSnapshot(envelope.AttemptId, command.ReservationRequestId, 1,
                BatchFixture.Now, command.Context.Policy, envelope);
            return Task.FromResult(new BatchReservationResult(BatchReservationStatus.Reserved, command.Context.Batch, attempt, null, "reserved"));
        };
        var result = await f.RunAsync(f.Coordinator());
        Assert.Equal(BatchDeliveryRunDisposition.NeedsRecovery, result.Disposition);
        // The reservation DTO itself rejects the foreign key/batch before returning. A lost or
        // rejected store return cannot prove that reservation did not commit, so recovery is required.
        Assert.Equal("reservation_result_unknown", result.ReasonCode);
        Assert.Empty(f.Channel.Submissions);
        Assert.Empty(f.Port.Results);
    }
}
