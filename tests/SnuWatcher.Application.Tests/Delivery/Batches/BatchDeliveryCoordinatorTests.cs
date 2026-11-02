using SnuWatcher.Application.Delivery;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Delivery.Batches;

public sealed class BatchDeliveryCoordinatorTests
{
    [Fact]
    public async Task BoundedFoldNeverReservesOrSendsHalfOfCapturedCutoff()
    {
        using var fixture = new CoordinatorFixture();
        fixture.Port.Project = (_, _) => Task.FromResult(new BatchProjection(
            fixture.Port.Projects.Count <= 2 ? BatchProjectionStatus.MoreToFold : BatchProjectionStatus.ReadyToEvaluate,
            BatchFixture.Batch(complete: fixture.Port.Projects.Count > 2), null));
        fixture.Port.Read = (at, _) => Task.FromResult(BatchFixture.Context(evaluatedAt: at,
            recipients: [BatchFixture.Recipient(), BatchFixture.Recipient(BatchFixture.SecondId, reasons: [OpeningReason.InitialAvailability])],
            subscriptions: [BatchFixture.Watch(), BatchFixture.Watch(BatchFixture.SecondId)]));
        var coordinator = fixture.Coordinator();

        var partial = await fixture.RunAsync(coordinator, pages: 2);
        Assert.Equal(BatchDeliveryRunDisposition.MoreToFold, partial.Disposition);
        Assert.Equal(0, fixture.Port.Reads);
        Assert.Empty(fixture.Port.Reservations);
        Assert.Empty(fixture.Channel.Envelopes);
        var complete = await fixture.RunAsync(coordinator, pages: 2);

        Assert.Equal(BatchDeliveryRunDisposition.ResultRecorded, complete.Disposition);
        Assert.True(complete.SubmittedThisCall);
        var envelope = Assert.Single(fixture.Channel.Envelopes);
        Assert.Equal(2, envelope.EligibleRecipients.Length);
        Assert.Equal(new[] { OpeningReason.InitialAvailability }, envelope.EligibleRecipients[1].Reasons);
        Assert.Equal(3, fixture.Port.Projects.Count);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("context")]
    [InlineData("replay")]
    public async Task UnresolvedAuthorityNeverAutomaticallyResolvesOrSends(string stage)
    {
        using var fixture = new CoordinatorFixture();
        if (stage == "project") fixture.Port.Project = (_, _) => Task.FromResult(new BatchProjection(
            BatchProjectionStatus.UnresolvedAttempt, BatchFixture.Batch(), BatchFixture.AttemptId));
        if (stage == "context") fixture.Port.Read = (at, _) => Task.FromResult(BatchFixture.Context(evaluatedAt: at, unresolved: BatchFixture.AttemptId));
        if (stage == "replay") fixture.Port.Reserve = (command, _) => Task.FromResult(new BatchReservationResult(
            BatchReservationStatus.Replayed, BatchFixture.Batch(), ReplayAttempt(command), null, "replayed"));

        var result = await fixture.RunAsync(fixture.Coordinator());

        Assert.Equal(BatchDeliveryRunDisposition.NeedsRecovery, result.Disposition);
        Assert.Equal(BatchFixture.AttemptId, result.AttemptId);
        Assert.Empty(fixture.Channel.Envelopes);
        Assert.Empty(fixture.Port.Results);
    }

    [Fact]
    public async Task ResolvedAcceptedReplayReturnsSavedReceiptWithoutAnotherOsCall()
    {
        using var fixture = new CoordinatorFixture();
        var receipt = new BatchAttemptReceipt(BatchFixture.AttemptId, BatchFixture.BatchId,
            BatchAttemptResultOutcome.OsAccepted, 2, 2, 0, 0, BatchFixture.Now.AddMinutes(-1));
        fixture.Port.Reserve = (command, _) => Task.FromResult(new BatchReservationResult(BatchReservationStatus.Replayed,
            BatchFixture.Batch(DeliveryBatchStatus.Closed), ReplayAttempt(command), null, "replayed", receipt));

        var result = await fixture.RunAsync(fixture.Coordinator());

        Assert.Same(receipt, result.Receipt);
        Assert.False(result.SubmittedThisCall);
        Assert.Null(result.ChannelOutcome);
        Assert.Empty(fixture.Channel.Envelopes);
        Assert.Empty(fixture.Port.Results);
    }

    [Fact]
    public async Task KnownAcceptedResultIsRetriedExactlyBeforeAnyOtherWorkForSection()
    {
        using var fixture = new CoordinatorFixture();
        fixture.Port.Record = (_, _) => throw new InvalidOperationException("private database details");
        var coordinator = fixture.Coordinator();
        var first = await fixture.RunAsync(coordinator);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(5);
        fixture.Port.Record = (command, token) =>
        {
            Assert.False(token.CanBeCanceled);
            return Task.FromResult(BatchPort.Receipt(command));
        };

        var flushed = await fixture.RunAsync(coordinator);

        Assert.Equal(BatchDeliveryRunDisposition.ResultNotRecorded, first.Disposition);
        Assert.Equal(BatchChannelOutcome.OsAccepted, first.ChannelOutcome);
        Assert.Null(first.Receipt);
        Assert.Equal(BatchDeliveryRunDisposition.ResultRecorded, flushed.Disposition);
        Assert.Equal(BatchChannelOutcome.OsAccepted, flushed.ChannelOutcome);
        Assert.False(flushed.SubmittedThisCall);
        Assert.Same(first.KnownResult, flushed.KnownResult);
        Assert.Same(fixture.Port.Results[0], fixture.Port.Results[1]);
        Assert.Equal(BatchFixture.Now, flushed.KnownResult!.RecordedAtUtc);
        Assert.Single(fixture.Channel.Envelopes);
        Assert.Single(fixture.Port.Projects);
        Assert.Single(fixture.Port.Reservations);
    }

    [Fact]
    public async Task FullKnownResultMapBlocksNewSectionButStillAllowsExistingFlush()
    {
        using var fixture = new CoordinatorFixture();
        fixture.Port.Record = (_, _) => throw new InvalidOperationException("record unavailable");
        var coordinator = fixture.Coordinator(capacity: 1);
        await fixture.RunAsync(coordinator);

        var blocked = await fixture.RunAsync(coordinator, key: BatchFixture.Key("013"));
        fixture.Port.Record = (command, _) => Task.FromResult(BatchPort.Receipt(command));
        var flushed = await fixture.RunAsync(coordinator);

        Assert.Equal(BatchDeliveryRunDisposition.PendingResultLimit, blocked.Disposition);
        Assert.Equal(BatchDeliveryRunDisposition.ResultRecorded, flushed.Disposition);
        Assert.Single(fixture.Channel.Envelopes);
        Assert.Single(fixture.Port.Reservations);
    }

    [Theory]
    [InlineData("cancel", "not_submitted_cancelled")]
    [InlineData("expiry", "not_submitted_eligibility_expired")]
    [InlineData("stale", "not_submitted_source_stale")]
    [InlineData("clock", "not_submitted_clock_changed")]
    public async Task ReservationFollowedByCancellationOrTimeBoundaryIsNotSubmitted(string mode, string reason)
    {
        using var fixture = new CoordinatorFixture();
        using var cancellation = new CancellationTokenSource();
        if (mode == "expiry")
        {
            fixture.Clock.Now = BatchFixture.Now.AddSeconds(-1);
            fixture.Port.Read = (at, _) => Task.FromResult(BatchFixture.Context(evaluatedAt: at,
                validUntil: BatchFixture.Now, subscriptions: [BatchFixture.Watch(expiry:
                    WatchExpiry.FromLocalDate(new DateOnly(2026, 11, 1), TimeZoneInfo.Utc))]));
        }
        var reserve = fixture.Port.Reserve;
        fixture.Port.Reserve = async (command, token) =>
        {
            var result = await reserve(command, token);
            if (mode == "cancel") cancellation.Cancel();
            if (mode == "expiry") fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);
            if (mode == "stale") fixture.Clock.Now = fixture.Clock.Now.AddMinutes(3);
            if (mode == "clock") fixture.Clock.Now = fixture.Clock.Now.AddMinutes(-1);
            return result;
        };
        fixture.Port.Record = (command, token) =>
        {
            Assert.False(token.CanBeCanceled);
            return Task.FromResult(BatchPort.Receipt(command));
        };

        var result = await fixture.RunAsync(fixture.Coordinator(), cancellation.Token);

        Assert.False(result.SubmittedThisCall);
        Assert.Equal(BatchAttemptResultOutcome.NotSubmitted, result.KnownResult!.Outcome);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Equal(2, result.Receipt!.PendingCount);
        Assert.Empty(fixture.Channel.Envelopes);
    }

    [Theory]
    [InlineData(BatchChannelOutcome.OsAccepted)]
    [InlineData(BatchChannelOutcome.RetryableFailure)]
    public async Task KnownChannelOutcomeSurvivesLateCancellationAndUsesUncancelledRecording(BatchChannelOutcome outcome)
    {
        using var fixture = new CoordinatorFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Channel.Send = (_, _) => { cancellation.Cancel(); return Task.FromResult(outcome); };
        fixture.Port.Record = (command, token) =>
        {
            Assert.False(token.CanBeCanceled);
            return Task.FromResult(BatchPort.Receipt(command));
        };

        var result = await fixture.RunAsync(fixture.Coordinator(), cancellation.Token);

        Assert.True(result.SubmittedThisCall);
        Assert.Equal(outcome, result.ChannelOutcome);
        Assert.NotNull(result.Receipt);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("cancel")]
    [InlineData("invalid")]
    public async Task InvocationWithoutAuthoritativeOutcomeIsInterruptedUnknownAfterDriverStops(string mode)
    {
        using var fixture = new CoordinatorFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Channel.Send = (_, token) => mode switch
        {
            "throw" => throw new InvalidOperationException("private driver information"),
            "cancel" => CancelStoppedDriver(token),
            _ => Task.FromResult((BatchChannelOutcome)99),
        };

        var result = await fixture.RunAsync(fixture.Coordinator(), cancellation.Token);

        Assert.True(result.SubmittedThisCall);
        Assert.Equal(BatchChannelOutcome.AcceptanceUnknown, result.ChannelOutcome);
        Assert.Equal(BatchAttemptResultOutcome.InterruptedUnknown, result.KnownResult!.Outcome);
        Assert.Equal("channel_result_unknown", result.ReasonCode);
        Assert.NotNull(result.Receipt);
        Task<BatchChannelOutcome> CancelStoppedDriver(CancellationToken token)
        {
            cancellation.Cancel();
            return Task.FromCanceled<BatchChannelOutcome>(token);
        }
    }

    [Theory]
    [InlineData("full", BatchDeliveryRunDisposition.Closed)]
    [InlineData("unknown", BatchDeliveryRunDisposition.NeedsFreshCheck)]
    [InlineData("fewer", BatchDeliveryRunDisposition.ResultRecorded)]
    public async Task LatestCountsDriveDeliveryInsteadOfOriginalTriggerContent(string state, BatchDeliveryRunDisposition expected)
    {
        using var fixture = new CoordinatorFixture();
        fixture.Port.Read = (at, _) => Task.FromResult(BatchFixture.Context(evaluatedAt: at,
            section: state == "unknown" ? BatchFixture.State(outcome: CheckOutcome.Unavailable)
                : BatchFixture.State(registered: state == "full" ? 120 : 119)));

        var result = await fixture.RunAsync(fixture.Coordinator());

        Assert.Equal(expected, result.Disposition);
        if (state == "fewer") Assert.Equal(1, Assert.Single(fixture.Channel.Envelopes).CurrentSeats);
        else Assert.Empty(fixture.Channel.Envelopes);
    }

    [Fact]
    public async Task ObserverRunsOutsideGateAndCannotTurnDesktopFailureIntoAcceptance()
    {
        using var fixture = new CoordinatorFixture();
        fixture.Channel.Send = (_, _) => Task.FromResult(BatchChannelOutcome.RetryableFailure);
        var entered = false;
        BatchDeliveryAudit? audit = null;
        var result = await fixture.RunAsync(fixture.Coordinator(value =>
        {
            audit = value;
            entered = fixture.Gate.Wait(0);
            if (entered) fixture.Gate.Release();
            throw new InvalidOperationException("private observer details");
        }));

        Assert.True(entered);
        Assert.True(result.ObserverFailed);
        Assert.Equal(BatchChannelOutcome.RetryableFailure, result.ChannelOutcome);
        Assert.Equal(2, result.Receipt!.PendingCount);
        Assert.NotNull(audit);
        Assert.Equal(BatchFixture.Key(), audit!.Key);
        Assert.Equal(BatchFixture.Now, audit.CompletedAtUtc);
    }

    [Fact]
    public async Task CallerCancellationBeforeReservationMakesNoAttemptAndReleasesGate()
    {
        using var fixture = new CoordinatorFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Port.Read = (at, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(BatchFixture.Context(evaluatedAt: at));
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync(fixture.Coordinator(), cancellation.Token));

        Assert.Empty(fixture.Port.Reservations);
        Assert.Empty(fixture.Channel.Envelopes);
        Assert.True(fixture.Gate.Wait(0));
        fixture.Gate.Release();
    }

    [Fact]
    public async Task SameSectionCallerCannotResubmitWhileFirstActualChannelIsStillRunning()
    {
        using var fixture = new CoordinatorFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource<BatchChannelOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reserved = false;
        var reserve = fixture.Port.Reserve;
        fixture.Port.Reserve = async (command, token) => { reserved = true; return await reserve(command, token); };
        fixture.Port.Project = (_, _) => Task.FromResult(reserved
            ? new BatchProjection(BatchProjectionStatus.UnresolvedAttempt, BatchFixture.Batch(), BatchFixture.AttemptId)
            : new BatchProjection(BatchProjectionStatus.ReadyToEvaluate, BatchFixture.Batch(), null));
        fixture.Channel.Send = (_, _) => { started.SetResult(); return stopped.Task; };
        var coordinator = fixture.Coordinator();
        var first = fixture.RunAsync(coordinator);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = fixture.RunAsync(coordinator);
        Assert.False(second.IsCompleted);
        Assert.False(fixture.Gate.Wait(0));
        stopped.SetResult(BatchChannelOutcome.OsAccepted);
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        var blocked = await second.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(BatchDeliveryRunDisposition.NeedsRecovery, blocked.Disposition);
        Assert.Single(fixture.Channel.Envelopes);
    }

    [Fact]
    public async Task NoWorkDoesNotReadOrReserveAndReportsNoSubmission()
    {
        using var fixture = new CoordinatorFixture();
        fixture.Port.Project = (_, _) => Task.FromResult(new BatchProjection(BatchProjectionStatus.NoWork, null, null));
        var result = await fixture.RunAsync(fixture.Coordinator());
        Assert.Equal(BatchDeliveryRunDisposition.NoWork, result.Disposition);
        Assert.False(result.SubmittedThisCall);
        Assert.Equal(0, fixture.Port.Reads);
        Assert.Empty(fixture.Port.Reservations);
    }

    [Theory]
    [InlineData(BatchReservationStatus.UnresolvedAttempt, BatchDeliveryRunDisposition.NeedsRecovery)]
    [InlineData(BatchReservationStatus.NeedsFreshCheck, BatchDeliveryRunDisposition.NeedsFreshCheck)]
    [InlineData(BatchReservationStatus.NeedsRevalidation, BatchDeliveryRunDisposition.NeedsRevalidation)]
    [InlineData(BatchReservationStatus.MoreToFold, BatchDeliveryRunDisposition.MoreToFold)]
    [InlineData(BatchReservationStatus.VersionConflict, BatchDeliveryRunDisposition.NeedsRevalidation)]
    [InlineData(BatchReservationStatus.RecipientLimitReached, BatchDeliveryRunDisposition.RecipientLimitReached)]
    public async Task SealRevalidationResponseCannotBeginChannel(BatchReservationStatus status, BatchDeliveryRunDisposition expected)
    {
        using var fixture = new CoordinatorFixture();
        fixture.Port.Reserve = (_, _) => Task.FromResult(new BatchReservationResult(status, BatchFixture.Batch(), null,
            status == BatchReservationStatus.UnresolvedAttempt ? BatchFixture.AttemptId : null, "seal_changed"));
        var result = await fixture.RunAsync(fixture.Coordinator());
        Assert.Equal(expected, result.Disposition);
        Assert.Empty(fixture.Channel.Envelopes);
        Assert.Empty(fixture.Port.Results);
    }

    [Theory]
    [InlineData("fold", BatchDeliveryRunDisposition.MoreToFold)]
    [InlineData("closed", BatchDeliveryRunDisposition.Closed)]
    [InlineData("overflow", BatchDeliveryRunDisposition.RecipientLimitReached)]
    [InlineData("paused", BatchDeliveryRunDisposition.NeedsRevalidation)]
    public async Task RefreshedContextCanBlockEarlierReadyProjection(string state, BatchDeliveryRunDisposition expected)
    {
        using var fixture = new CoordinatorFixture();
        fixture.Port.Read = (at, _) => Task.FromResult(state switch
        {
            "fold" => BatchFixture.Context(batch: BatchFixture.Batch(complete: false), evaluatedAt: at),
            "closed" => BatchFixture.Context(batch: BatchFixture.Batch(DeliveryBatchStatus.Closed),
                coverage: BatchFixture.Coverage(0, 0, 0), recipients: [], subscriptions: [], pending: 0, evaluatedAt: at),
            "overflow" => BatchFixture.Context(overflow: true, evaluatedAt: at),
            _ => BatchFixture.Context(evaluatedAt: at, subscriptions: [BatchFixture.Watch(status: WatchStatus.Paused)]),
        });
        var result = await fixture.RunAsync(fixture.Coordinator());
        Assert.Equal(expected, result.Disposition);
        Assert.Empty(fixture.Port.Reservations);
        Assert.Empty(fixture.Channel.Envelopes);
    }

    [Fact]
    public async Task LostReservationReturnNeverInvokesAndNextCallDiscoversUnresolvedAuthority()
    {
        using var fixture = new CoordinatorFixture();
        fixture.Port.Reserve = (_, _) => throw new InvalidOperationException("lost seal return");
        var coordinator = fixture.Coordinator();
        var lost = await fixture.RunAsync(coordinator);
        fixture.Port.Project = (_, _) => Task.FromResult(new BatchProjection(BatchProjectionStatus.UnresolvedAttempt,
            BatchFixture.Batch(), BatchFixture.AttemptId));
        var blocked = await fixture.RunAsync(coordinator);
        Assert.Equal("reservation_result_unknown", lost.ReasonCode);
        Assert.Equal(BatchDeliveryRunDisposition.NeedsRecovery, blocked.Disposition);
        Assert.Single(fixture.Port.Reservations);
        Assert.Empty(fixture.Channel.Envelopes);
        Assert.Empty(fixture.Port.Results);
    }

    [Fact]
    public async Task MismatchedReceiptCannotDiscardKnownAcceptanceOrAllowAnotherSubmission()
    {
        using var fixture = new CoordinatorFixture();
        fixture.Port.Record = (command, _) => Task.FromResult(new BatchAttemptReceipt(command.AttemptId, BatchFixture.BatchId,
            BatchAttemptResultOutcome.RetryableFailure, 2, 0, 2, 0, command.RecordedAtUtc));
        var coordinator = fixture.Coordinator();
        var mismatch = await fixture.RunAsync(coordinator);
        fixture.Port.Record = (command, _) => Task.FromResult(BatchPort.Receipt(command));
        var flush = await fixture.RunAsync(coordinator);
        Assert.Equal("result_receipt_mismatch", mismatch.ReasonCode);
        Assert.Equal(BatchChannelOutcome.OsAccepted, mismatch.ChannelOutcome);
        Assert.Equal(BatchDeliveryRunDisposition.ResultRecorded, flush.Disposition);
        Assert.Same(mismatch.KnownResult, flush.KnownResult);
        Assert.Single(fixture.Channel.Envelopes);
        Assert.Single(fixture.Port.Reservations);
    }

    [Fact]
    public async Task NotSubmittedWriteFailureAlsoRetainsExactZeroCostCommand()
    {
        using var fixture = new CoordinatorFixture();
        var reserve = fixture.Port.Reserve;
        fixture.Port.Reserve = async (command, token) =>
        {
            var result = await reserve(command, token);
            fixture.Clock.Now = fixture.Clock.Now.AddMinutes(3);
            return result;
        };
        fixture.Port.Record = (_, _) => throw new InvalidOperationException("record unavailable");
        var coordinator = fixture.Coordinator();
        var first = await fixture.RunAsync(coordinator);
        fixture.Port.Record = (command, _) => Task.FromResult(BatchPort.Receipt(command));
        fixture.Clock.Now = fixture.Clock.Now.AddDays(1);
        var flush = await fixture.RunAsync(coordinator);
        Assert.Equal(BatchAttemptResultOutcome.NotSubmitted, first.KnownResult!.Outcome);
        Assert.Same(first.KnownResult, flush.KnownResult);
        Assert.Equal(2, flush.Receipt!.PendingCount);
        Assert.Empty(fixture.Channel.Envelopes);
        Assert.Single(fixture.Port.Reservations);
    }

    [Fact]
    public async Task ConcurrentFoldRechecksPendingResultMapAfterReacquiringGate()
    {
        using var fixture = new CoordinatorFixture();
        var folding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<BatchProjection>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Port.Project = (_, _) =>
        {
            if (fixture.Port.Projects.Count == 1) { folding.SetResult(); return release.Task; }
            return Task.FromResult(new BatchProjection(BatchProjectionStatus.ReadyToEvaluate, BatchFixture.Batch(), null));
        };
        fixture.Port.Record = (command, _) => fixture.Port.Results.Count == 1
            ? throw new InvalidOperationException("temporary record failure") : Task.FromResult(BatchPort.Receipt(command));
        var coordinator = fixture.Coordinator();
        var first = fixture.RunAsync(coordinator);
        await folding.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await fixture.RunAsync(coordinator);
        Assert.Equal(BatchDeliveryRunDisposition.ResultNotRecorded, second.Disposition);
        release.SetResult(new(BatchProjectionStatus.ReadyToEvaluate, BatchFixture.Batch(), null));
        var resumed = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BatchDeliveryRunDisposition.ResultRecorded, resumed.Disposition);
        Assert.False(resumed.SubmittedThisCall);
        Assert.Single(fixture.Channel.Envelopes);
        Assert.Single(fixture.Port.Reservations);
    }

    [Theory]
    [InlineData("counts")]
    [InlineData("policy")]
    public async Task ChangedReservedFactsAreNotSubmitted(string field)
    {
        using var fixture = new CoordinatorFixture();
        fixture.Port.Reserve = (command, _) =>
        {
            var content = command.Decision.Content!;
            var envelope = new BatchDeliveryEnvelope(BatchFixture.BatchId, BatchFixture.AttemptId, content.Key,
                field == "counts" ? new SeatCount(117, 120) : content.CurrentCounts, content.CurrentSuccessAtUtc,
                content.CurrentSourceOrdinal, content.EligibleRecipients, content.Coverage);
            var attempt = new BatchAttemptSnapshot(BatchFixture.AttemptId, command.ReservationRequestId, 1, command.EvaluatedAtUtc,
                field == "policy" ? new BatchDeliveryPolicy(TimeSpan.FromMinutes(1), 3) : command.Context.Policy, envelope);
            return Task.FromResult(new BatchReservationResult(BatchReservationStatus.Reserved, command.Context.Batch, attempt, null, "reserved"));
        };
        var result = await fixture.RunAsync(fixture.Coordinator());
        Assert.Equal(BatchAttemptResultOutcome.NotSubmitted, result.KnownResult!.Outcome);
        Assert.Equal("not_submitted_facts_changed", result.ReasonCode);
        Assert.Empty(fixture.Channel.Envelopes);
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(201, 8)]
    [InlineData(128, 0)]
    [InlineData(128, 129)]
    public async Task ResourceBoundsRejectUnboundedFoldRequestsBeforeStoreCalls(int pageSize, int pages)
    {
        using var fixture = new CoordinatorFixture();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Coordinator().TryDeliverSectionAsync(
            BatchFixture.Key(), BatchFixture.Policy(), pageSize, pages));
        Assert.Empty(fixture.Port.Projects);
        Assert.Empty(fixture.Channel.Envelopes);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("batch")]
    public async Task ForeignReservationResponseIsNotSubmissionOrResultAuthority(string field)
    {
        using var fixture = new CoordinatorFixture();
        fixture.Port.Reserve = (command, _) =>
        {
            var batch = field == "batch" ? new DeliveryBatchSnapshot(Guid.NewGuid(), BatchFixture.Key(), 4,
                DeliveryBatchStatus.Open, 2, 2, true, BatchFixture.Now) : BatchFixture.Batch();
            var content = command.Decision.Content!;
            var envelope = new BatchDeliveryEnvelope(batch.Id, BatchFixture.AttemptId, content.Key,
                content.CurrentCounts, content.CurrentSuccessAtUtc, content.CurrentSourceOrdinal, content.EligibleRecipients, content.Coverage);
            var attempt = new BatchAttemptSnapshot(BatchFixture.AttemptId,
                field == "request" ? Guid.NewGuid() : command.ReservationRequestId, 1, command.EvaluatedAtUtc, command.Context.Policy, envelope);
            return Task.FromResult(new BatchReservationResult(BatchReservationStatus.Reserved, batch, attempt, null, "reserved"));
        };
        var result = await fixture.RunAsync(fixture.Coordinator());
        Assert.Equal(BatchDeliveryRunDisposition.NeedsRecovery, result.Disposition);
        Assert.Equal("reservation_authority_mismatch", result.ReasonCode);
        Assert.Empty(fixture.Channel.Envelopes);
        Assert.Empty(fixture.Port.Results);
    }

    [Fact]
    public async Task LostCommittedResultRemainsDiscoverableWhenDatabaseWorkPageIsEmptyAndKnownMapIsFull()
    {
        using var fixture = new CoordinatorFixture();
        BatchAttemptReceipt? committed = null;
        fixture.Port.Record = (command, _) =>
        {
            if (committed is not null) return Task.FromResult(committed);
            committed = BatchPort.Receipt(command);
            throw new InvalidOperationException("commit returned no result");
        };
        var coordinator = fixture.Coordinator(capacity: 1);
        var first = await fixture.RunAsync(coordinator);
        Assert.Equal(BatchDeliveryRunDisposition.ResultNotRecorded, first.Disposition);
        Assert.Empty(await fixture.Port.ReadPendingDeliverySectionsAsync(1, default));
        Assert.Equal(BatchDeliveryRunDisposition.PendingResultLimit,
            (await fixture.RunAsync(coordinator, key: BatchFixture.Key("013"))).Disposition);
        var retained = await coordinator.ReadPendingKnownSectionsAsync();
        Assert.Equal(BatchFixture.Key(), Assert.Single(retained));
        Assert.Throws<NotSupportedException>(() => ((IList<SectionKey>)retained).Clear());
        fixture.Clock.Now = fixture.Clock.Now.AddHours(1);
        var flush = await fixture.RunAsync(coordinator, key: retained[0]);
        Assert.Same(committed, flush.Receipt);
        Assert.Same(first.KnownResult, flush.KnownResult);
        Assert.False(flush.SubmittedThisCall);
        Assert.Empty(await coordinator.ReadPendingKnownSectionsAsync());
        Assert.Single(fixture.Channel.Envelopes);
    }

    private static BatchAttemptSnapshot ReplayAttempt(PreparedBatchAttempt command) => new(BatchFixture.AttemptId,
        command.ReservationRequestId, 1, BatchFixture.Now, command.Context.Policy, BatchFixture.Envelope());
}
