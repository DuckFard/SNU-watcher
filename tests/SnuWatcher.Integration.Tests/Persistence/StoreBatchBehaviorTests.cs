using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreBatchBehaviorTests
{
    private static readonly BatchDeliveryPolicy Policy = new(TimeSpan.FromMinutes(2), 3);
    private static IBatchDeliveryStore Batches(StoreFixture f) => (IBatchDeliveryStore)(object)f.Store;

    [Fact]
    public async Task IncreaseAndInitialNoticeAcrossPagesUnionRecipientsAndAckExactlyOnce()
    {
        await using var f = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        var a = await f.AddAsync(key, false, "A 서울 'quoted'");
        await f.ObserveAsync(key, 120);
        await f.ObserveAsync(key, 119);
        var b = await f.AddAsync(key, true, "B");
        await f.ObserveAsync(key, 119);
        var one = await Batches(f).ProjectDeliveryBatchAsync(new(key, 1, f.Clock.GetUtcNow()), default);
        Assert.Equal(BatchProjectionStatus.MoreToFold, one.Status);
        var incomplete = await PrepareAsync(f, one.Batch!.Id);
        Assert.Equal(BatchReservationStatus.MoreToFold, (await Batches(f).TryReserveBatchAttemptAsync(incomplete, default)).Status);
        var two = await Batches(f).ProjectDeliveryBatchAsync(new(key, 1, f.Clock.GetUtcNow()), default);
        Assert.Equal(one.Batch.Id, two.Batch!.Id);
        Assert.Equal(BatchProjectionStatus.ReadyToEvaluate, two.Status);
        var prepared = await PrepareAsync(f, two.Batch.Id);
        Assert.Equal(2, prepared.Context.EligibleCoverage.EventCount);
        Assert.Equal(new[] { a.Id, b.Id }.Order(), prepared.Context.EligibleRecipients.Select(r => r.SubscriptionId).Order());
        var reserved = await Batches(f).TryReserveBatchAttemptAsync(prepared, default);
        Assert.Equal(BatchReservationStatus.Reserved, reserved.Status);
        var attempt = reserved.Attempt!;
        Assert.Equal(two.Batch.Id.ToString("N"), attempt.Envelope.NativeDedupeKey);
        var coverage = await Batches(f).ReadBatchAttemptCoverageAsync(attempt.AttemptId, 0, 1, default);
        Assert.Single(coverage);
        Assert.Single(await Batches(f).ReadBatchAttemptCoverageAsync(attempt.AttemptId, coverage[0].TriggerOrdinal, 1, default));
        var command = new BatchAttemptResultCommand(attempt.AttemptId, BatchAttemptResultOutcome.OsAccepted, "os_accepted", f.Clock.GetUtcNow());
        var receipt = await Batches(f).RecordBatchAttemptResultAsync(command, default);
        Assert.Equal(2, receipt.AcceptedCount);
        Assert.Empty(await f.Store.ReadPendingDeliveriesAsync(20, default));
        await f.ReopenAsync();
        Assert.Equal(receipt, await Batches(f).RecordBatchAttemptResultAsync(command, default));
        var replay = await Batches(f).TryReserveBatchAttemptAsync(prepared, default);
        Assert.Equal(BatchReservationStatus.Replayed, replay.Status);
        Assert.Equal(receipt, replay.Resolution);
        Assert.Equal(attempt.AttemptId, replay.Attempt!.AttemptId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Batches(f).RecordBatchAttemptResultAsync(
            new(attempt.AttemptId, BatchAttemptResultOutcome.RetryableFailure, "desktop_error", f.Clock.GetUtcNow()), default));
    }

    [Fact]
    public async Task PollDuringSubmissionDoesNotInvalidateAcceptanceOrAckNewEvent()
    {
        await using var f = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        await f.AddAsync(key);
        await f.ObserveAsync(key, 119);
        var first = await ReserveAsync(f, key);
        await f.AddAsync(key, true, "New during OS send");
        await f.ObserveAsync(key, 119);
        var unresolved = await Batches(f).ProjectDeliveryBatchAsync(new(key, 20, f.Clock.GetUtcNow()), default);
        Assert.Equal(BatchProjectionStatus.UnresolvedAttempt, unresolved.Status);
        var receipt = await Batches(f).RecordBatchAttemptResultAsync(new(first.AttemptId,
            BatchAttemptResultOutcome.OsAccepted, "os_accepted", f.Clock.GetUtcNow()), default);
        Assert.Equal(1, receipt.AcceptedCount);
        Assert.Single(await f.Store.ReadPendingDeliveriesAsync(20, default));
        var next = await ReserveAsync(f, key);
        Assert.NotEqual(first.Envelope.BatchId, next.Envelope.BatchId);
        Assert.Single(next.Envelope.EligibleRecipients);
    }

    [Fact]
    public async Task NewlyFoldedEventDoesNotInheritOlderFailureBudget()
    {
        await using var f = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        var a = await f.AddAsync(key);
        await f.ObserveAsync(key, 119);
        var first = await ReserveAsync(f, key);
        await FailAsync(f, first);
        var second = await ReserveAsync(f, key);
        Assert.Equal(first.Envelope.NativeDedupeKey, second.Envelope.NativeDedupeKey);
        await FailAsync(f, second);
        var b = await f.AddAsync(key, true, "New B");
        await f.ObserveAsync(key, 119);
        var third = await ReserveAsync(f, key);
        Assert.Equal(2, third.Envelope.Coverage.EventCount);
        var result = await FailAsync(f, third);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.PendingCount);
        var remaining = Assert.Single(await f.Store.ReadPendingDeliveriesAsync(20, default));
        Assert.Equal(1, remaining.Attempts);
        Assert.Equal(b.Id.ToString("N"), Assert.Single(remaining.Trigger.Recipients).SubscriptionId);
        var fourth = await ReserveAsync(f, key);
        Assert.Equal(first.Envelope.NativeDedupeKey, fourth.Envelope.NativeDedupeKey);
        Assert.Equal(b.Id, Assert.Single(fourth.Envelope.EligibleRecipients).SubscriptionId);
        Assert.DoesNotContain(fourth.Envelope.EligibleRecipients, r => r.SubscriptionId == a.Id);
    }

    [Fact]
    public async Task UnknownRecoveryAndNotSubmittedKeepStableKeyAndDistinctBudgetEvidence()
    {
        await using var f = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        await f.AddAsync(key);
        await f.ObserveAsync(key, 119);
        var first = await ReserveAsync(f, key);
        await Batches(f).RecordBatchAttemptResultAsync(new(first.AttemptId, BatchAttemptResultOutcome.NotSubmitted,
            "canceled_before_call", f.Clock.GetUtcNow()), default);
        Assert.Equal(0, Assert.Single(await f.Store.ReadPendingDeliveriesAsync(20, default)).Attempts);
        var second = await ReserveAsync(f, key);
        await f.ReopenAsync();
        var recovered = await Batches(f).ProjectDeliveryBatchAsync(new(key, 20, f.Clock.GetUtcNow()), default);
        Assert.Equal(second.AttemptId, recovered.UnresolvedAttemptId);
        await Batches(f).RecordBatchAttemptResultAsync(new(second.AttemptId, BatchAttemptResultOutcome.InterruptedUnknown,
            "process_interrupted", f.Clock.GetUtcNow()), default);
        Assert.Equal(1, Assert.Single(await f.Store.ReadPendingDeliveriesAsync(20, default)).Attempts);
        var third = await ReserveAsync(f, key);
        Assert.Equal(first.Envelope.NativeDedupeKey, third.Envelope.NativeDedupeKey);
        Assert.NotEqual(second.AttemptId, third.AttemptId);
    }

    [Fact]
    public async Task IndividualWriterCannotChangeBatchOwnedEvent()
    {
        await using var f = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        await f.AddAsync(key);
        await f.ObserveAsync(key, 119);
        var pending = Assert.Single(await f.Store.ReadPendingDeliveriesAsync(20, default));
        await ReserveAsync(f, key);
        Assert.Equal(DeliveryWriteResult.VersionConflict, await f.Store.RecordDeliveryResultAsync(new(pending.EventId,
            pending.DeliveryRevision, DeliveryStatus.Superseded, 0, f.Clock.GetUtcNow()), default));
    }

    [Fact]
    public async Task PollFullAfterReservationStillRecordsActualAcceptance()
    {
        await using var f = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        await f.AddAsync(key);
        await f.ObserveAsync(key, 119);
        var reserved = await ReserveAsync(f, key);
        await f.ObserveAsync(key, 120);
        var receipt = await Batches(f).RecordBatchAttemptResultAsync(new(reserved.AttemptId,
            BatchAttemptResultOutcome.OsAccepted, "os_accepted", f.Clock.GetUtcNow()), default);
        Assert.Equal(1, receipt.AcceptedCount);
        Assert.Equal(0, (await f.Store.ReadSectionAsync(key, default)).Baseline!.Seats.ApparentSeats);
    }

    [Fact]
    public async Task RepeatedProjectionCannotMoveTheCapturedCutoffBeforeTheFirstSeal()
    {
        await using var f = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        await f.AddAsync(key);
        await f.ObserveAsync(key, 119);
        var original = await Batches(f).ProjectDeliveryBatchAsync(new(key, 20, f.Clock.GetUtcNow()), default);
        await f.AddAsync(key, true, "After cutoff");
        await f.ObserveAsync(key, 119);
        var repeated = await Batches(f).ProjectDeliveryBatchAsync(new(key, 20, f.Clock.GetUtcNow()), default);
        Assert.Equal(original.Batch!.FoldCutoffOrdinal, repeated.Batch!.FoldCutoffOrdinal);
        Assert.Single((await PrepareAsync(f, repeated.Batch.Id)).Context.EligibleRecipients);
    }

    [Fact]
    public async Task RetryFoldCapturesOneNewCutoffAndKeepsItFixedUntilTheNextAttempt()
    {
        await using var f = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        await f.AddAsync(key);
        await f.ObserveAsync(key, 119);
        await FailAsync(f, await ReserveAsync(f, key));
        await f.AddAsync(key, true, "B before retry cutoff");
        await f.ObserveAsync(key, 119);
        var retry = await Batches(f).ProjectDeliveryBatchAsync(new(key, 20, f.Clock.GetUtcNow()), default);
        Assert.Equal(2, (await PrepareAsync(f, retry.Batch!.Id)).Context.EligibleCoverage.EventCount);
        await f.AddAsync(key, true, "C after retry cutoff");
        await f.ObserveAsync(key, 119);
        var repeated = await Batches(f).ProjectDeliveryBatchAsync(new(key, 20, f.Clock.GetUtcNow()), default);
        Assert.Equal(retry.Batch.FoldCutoffOrdinal, repeated.Batch!.FoldCutoffOrdinal);
        Assert.Equal(2, (await PrepareAsync(f, repeated.Batch.Id)).Context.EligibleCoverage.EventCount);
    }

    [Fact]
    public async Task ChangedCatalogOrExpiryBetweenReadAndSealCreatesNoAttempt()
    {
        await using var f = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        var watch = await f.AddAsync(key);
        await f.ObserveAsync(key, 119);
        var projection = await Batches(f).ProjectDeliveryBatchAsync(new(key, 20, f.Clock.GetUtcNow()), default);
        var prepared = await PrepareAsync(f, projection.Batch!.Id);
        await f.Store.ChangeSubscriptionAsync(new RenameSubscription(watch.Id, watch.DefinitionRevision, f.Clock.GetUtcNow(), "Renamed"), default);
        Assert.Equal(BatchReservationStatus.VersionConflict, (await Batches(f).TryReserveBatchAttemptAsync(prepared, default)).Status);
        Assert.Single(await f.Store.ReadPendingDeliveriesAsync(20, default));
    }

    internal static async Task<PreparedBatchAttempt> PrepareAsync(StoreFixture f, Guid batchId, Guid? requestId = null)
    {
        var context = await Batches(f).ReadDeliveryBatchContextAsync(batchId, Policy, f.Clock.GetUtcNow(), default);
        return new(requestId ?? Guid.NewGuid(), context, BatchDeliveryPlanner.Plan(context, f.Clock.GetUtcNow()), f.Clock.GetUtcNow());
    }

    internal static async Task<BatchAttemptSnapshot> ReserveAsync(StoreFixture f, SectionKey key)
    {
        BatchProjection projection;
        do { projection = await Batches(f).ProjectDeliveryBatchAsync(new(key, 20, f.Clock.GetUtcNow()), default); }
        while (projection.Status == BatchProjectionStatus.MoreToFold);
        var result = await Batches(f).TryReserveBatchAttemptAsync(await PrepareAsync(f, projection.Batch!.Id), default);
        Assert.Equal(BatchReservationStatus.Reserved, result.Status);
        return result.Attempt!;
    }

    private static Task<BatchAttemptReceipt> FailAsync(StoreFixture f, BatchAttemptSnapshot attempt) =>
        Batches(f).RecordBatchAttemptResultAsync(new(attempt.AttemptId, BatchAttemptResultOutcome.RetryableFailure,
            "desktop_error", f.Clock.GetUtcNow()), default);
}
