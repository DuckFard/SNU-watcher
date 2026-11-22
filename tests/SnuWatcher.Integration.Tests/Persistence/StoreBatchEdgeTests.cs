using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreBatchEdgeTests
{
    private static readonly BatchDeliveryPolicy Policy = new(TimeSpan.FromMinutes(2), 3);
    private static IBatchDeliveryStore Batch(StoreFixture f) => f.Store;

    [Fact]
    public async Task ProjectionFaultLeavesNoClaimsOrOpenBatchAfterReopen()
    {
        var armed = false;
        await using var f = await StoreFixture.CreateAsync(stage =>
        { if (armed && stage == SqliteStoreWriteStage.BatchProjected) throw new EdgeFault(); });
        await TwoEventsAsync(f);
        var before = await f.Store.ReadPendingDeliveriesAsync(20, default);
        armed = true;
        await Assert.ThrowsAsync<EdgeFault>(() => Batch(f).ProjectDeliveryBatchAsync(new(StoreFixture.Key(), 1, f.Clock.GetUtcNow()), default));
        armed = false;
        await f.ReopenAsync();
        Assert.Null(Assert.Single(await Batch(f).ReadPendingDeliverySectionsAsync(20, default)).OpenBatchId);
        await AssertPendingUnchangedAsync(f, before);
        var first = await ProjectAsync(f);
        var repeated = await ProjectAsync(f);
        Assert.Equal(first.Id, repeated.Id);
        Assert.Equal(2, (await PrepareAsync(f, first.Id)).Context.EligibleCoverage.EventCount);
    }

    [Theory]
    [InlineData(SqliteStoreWriteStage.BatchMembers)]
    [InlineData(SqliteStoreWriteStage.BatchReserved)]
    public async Task SealFaultAfterPartialMemberWorkRollsBackAuthorityAndReusesReservationRequest(SqliteStoreWriteStage target)
    {
        var armed = false; var members = 0;
        await using var f = await StoreFixture.CreateAsync(stage =>
        {
            if (!armed) return;
            if (stage == SqliteStoreWriteStage.BatchMembers) members++;
            if (stage == target && (target != SqliteStoreWriteStage.BatchMembers || members == 2)) throw new EdgeFault();
        });
        await TwoEventsAsync(f);
        var batch = await ProjectAsync(f);
        var prepared = await PrepareAsync(f, batch.Id);
        var before = await f.Store.ReadPendingDeliveriesAsync(20, default);
        armed = true;
        await Assert.ThrowsAsync<EdgeFault>(() => Batch(f).TryReserveBatchAttemptAsync(prepared, default));
        armed = false;
        await f.ReopenAsync();
        await AssertPendingUnchangedAsync(f, before);
        var recovered = await ProjectAsync(f);
        Assert.Equal(batch.Id, recovered.Id);
        Assert.Equal(batch.Revision, recovered.Revision);
        var context = (await PrepareAsync(f, batch.Id)).Context;
        Assert.Null(context.UnresolvedAttemptId);
        Assert.Equal(2, context.EligibleCoverage.EventCount);
        var reserved = await Batch(f).TryReserveBatchAttemptAsync(prepared, default);
        Assert.Equal(BatchReservationStatus.Reserved, reserved.Status);
        Assert.Equal(prepared.ReservationRequestId, reserved.Attempt!.ReservationRequestId);
        Assert.Equal(2, (await Batch(f).ReadBatchAttemptCoverageAsync(reserved.Attempt.AttemptId, 0, 20, default)).Length);
    }

    [Theory]
    [InlineData(SqliteStoreWriteStage.BatchMembers)]
    [InlineData(SqliteStoreWriteStage.BatchResult)]
    public async Task ResultFaultRollsBackEveryAckHistoryAndResolutionThenRetriesExactlyOnce(SqliteStoreWriteStage target)
    {
        var armed = false; var members = 0;
        await using var f = await StoreFixture.CreateAsync(stage =>
        {
            if (!armed) return;
            if (stage == SqliteStoreWriteStage.BatchMembers) members++;
            if (stage == target && (target != SqliteStoreWriteStage.BatchMembers || members == 2)) throw new EdgeFault();
        });
        await TwoEventsAsync(f);
        var reserved = await ReserveAsync(f);
        var before = await f.Store.ReadPendingDeliveriesAsync(20, default);
        var command = new BatchAttemptResultCommand(reserved.AttemptId, BatchAttemptResultOutcome.OsAccepted, "os_accepted", f.Clock.GetUtcNow());
        armed = true;
        await Assert.ThrowsAsync<EdgeFault>(() => Batch(f).RecordBatchAttemptResultAsync(command, default));
        armed = false;
        await f.ReopenAsync();
        await AssertPendingUnchangedAsync(f, before);
        var unresolved = await Batch(f).ProjectDeliveryBatchAsync(new(StoreFixture.Key(), 20, f.Clock.GetUtcNow()), default);
        Assert.Equal(BatchProjectionStatus.UnresolvedAttempt, unresolved.Status);
        Assert.Equal(reserved.AttemptId, unresolved.UnresolvedAttemptId);
        var receipt = await Batch(f).RecordBatchAttemptResultAsync(command, default);
        Assert.Equal(2, receipt.AcceptedCount);
        Assert.Empty(await f.Store.ReadPendingDeliveriesAsync(20, default));
        await f.ReopenAsync();
        Assert.Equal(receipt, await Batch(f).RecordBatchAttemptResultAsync(command, default));
        foreach (var pending in before)
        {
            var saved = (await f.Store.ReadDeliveryContextAsync(pending.EventId, default)).Delivery;
            Assert.Equal(pending.DeliveryRevision + 1, saved.DeliveryRevision);
            Assert.Equal(1, saved.Attempts);
            Assert.Equal(DeliveryStatus.OsAccepted, saved.Status);
        }
    }

    [Fact]
    public async Task ConcurrentStoreProjectionsClaimOneBatchAndReservationsGrantOneStableAuthority()
    {
        await using var f = await StoreFixture.CreateAsync();
        await TwoEventsAsync(f);
        await using var other = await SqliteWatchStore.OpenAsync(new SqliteConnectionFactory(f.DatabasePath), f.Clock);
        var command = new ProjectDeliveryBatchCommand(StoreFixture.Key(), 1, f.Clock.GetUtcNow());
        var projections = await Task.WhenAll(Batch(f).ProjectDeliveryBatchAsync(command, default),
            ((IBatchDeliveryStore)other).ProjectDeliveryBatchAsync(command, default));
        Assert.Single(projections.Select(value => value.Batch!.Id).Distinct());
        var batch = await ProjectAsync(f);
        Assert.Equal(2, (await PrepareAsync(f, batch.Id)).Context.PendingCount);
        var first = await PrepareAsync(f, batch.Id);
        var second = await PrepareAsync(f, batch.Id);
        var results = await Task.WhenAll(Batch(f).TryReserveBatchAttemptAsync(first, default),
            ((IBatchDeliveryStore)other).TryReserveBatchAttemptAsync(second, default));
        var won = Assert.Single(results, result => result.Status == BatchReservationStatus.Reserved);
        var blocked = Assert.Single(results, result => result.Status == BatchReservationStatus.UnresolvedAttempt);
        Assert.Equal(won.Attempt!.AttemptId, blocked.UnresolvedAttemptId);
        Assert.Equal(batch.Id.ToString("N"), won.Attempt.Envelope.NativeDedupeKey);
        Assert.Equal(2, (await Batch(f).ReadBatchAttemptCoverageAsync(won.Attempt.AttemptId, 0, 20, default)).Length);
    }

    [Fact]
    public async Task SameReservationRequestAcrossStoreInstancesReplaysTheOnlyFrozenAttempt()
    {
        await using var f = await StoreFixture.CreateAsync();
        await TwoEventsAsync(f);
        var batch = await ProjectAsync(f);
        var prepared = await PrepareAsync(f, batch.Id);
        await using var other = await SqliteWatchStore.OpenAsync(new SqliteConnectionFactory(f.DatabasePath), f.Clock);
        var results = await Task.WhenAll(Batch(f).TryReserveBatchAttemptAsync(prepared, default),
            ((IBatchDeliveryStore)other).TryReserveBatchAttemptAsync(prepared, default));
        Assert.Single(results, result => result.Status == BatchReservationStatus.Reserved);
        Assert.Single(results, result => result.Status == BatchReservationStatus.Replayed);
        Assert.Single(results.Select(result => result.Attempt!.AttemptId).Distinct());
        Assert.All(results, result => Assert.Null(result.Resolution));
    }

    [Theory]
    [InlineData("unknown", BatchReservationStatus.NeedsFreshCheck)]
    [InlineData("stale", BatchReservationStatus.NeedsFreshCheck)]
    [InlineData("full", BatchReservationStatus.Closed)]
    [InlineData("paused", BatchReservationStatus.Closed)]
    [InlineData("removed", BatchReservationStatus.Closed)]
    [InlineData("expired", BatchReservationStatus.Closed)]
    public async Task StoreRecomputesNonsendableClassificationWithoutCreatingAuthority(string change, BatchReservationStatus expected)
    {
        await using var f = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        var watch = await f.AddAsync(key);
        await f.ObserveAsync(key, 119);
        var pending = Assert.Single(await f.Store.ReadPendingDeliveriesAsync(20, default));
        var batch = await ProjectAsync(f);
        if (change == "unknown") await f.CommitAsync(f.Unknown(await f.IssueAsync(key), key));
        if (change == "stale") f.Clock.Advance(TimeSpan.FromMinutes(3));
        if (change == "full") await f.ObserveAsync(key, 120);
        if (change == "paused") await f.Store.ChangeSubscriptionAsync(new PauseSubscription(watch.Id, watch.DefinitionRevision, f.Clock.GetUtcNow()), default);
        if (change == "removed") await f.Store.ChangeSubscriptionAsync(new RemoveSubscription(watch.Id, watch.DefinitionRevision, f.Clock.GetUtcNow()), default);
        if (change == "expired") await f.Store.ChangeSubscriptionAsync(new SetSubscriptionExpiry(watch.Id, watch.DefinitionRevision,
            f.Clock.GetUtcNow(), WatchExpiry.FromLocalDate(new DateOnly(2026, 10, 31), TimeZoneInfo.Utc)), default);
        var result = await Batch(f).TryReserveBatchAttemptAsync(await PrepareAsync(f, batch.Id), default);
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Attempt);
        Assert.Null(result.UnresolvedAttemptId);
        var delivery = (await f.Store.ReadDeliveryContextAsync(pending.EventId, default)).Delivery;
        Assert.Equal(expected == BatchReservationStatus.Closed ? DeliveryStatus.Superseded : DeliveryStatus.Pending, delivery.Status);
        Assert.Equal(0, delivery.Attempts);
        await f.ReopenAsync();
        Assert.Equal(delivery.Status, (await f.Store.ReadDeliveryContextAsync(pending.EventId, default)).Delivery.Status);
    }

    [Fact]
    public async Task ExpiryBetweenAggregationAndSealConflictsThenFreshReadClosesWithoutSending()
    {
        await using var f = await StoreFixture.CreateAsync();
        f.Clock.Set(new(2026, 11, 1, 14, 59, 59, TimeSpan.Zero));
        var key = StoreFixture.Key();
        await f.Store.CreateSubscriptionAsync(f.NewSection(key, expiry: WatchExpiry.FromLocalDate(new DateOnly(2026, 11, 1))), default);
        await f.ObserveAsync(key, 119);
        var batch = await ProjectAsync(f);
        var prepared = await PrepareAsync(f, batch.Id);
        Assert.Equal(BatchPlanDisposition.Send, prepared.Decision.Disposition);
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(BatchReservationStatus.VersionConflict, (await Batch(f).TryReserveBatchAttemptAsync(prepared, default)).Status);
        Assert.Single(await f.Store.ReadPendingDeliveriesAsync(20, default));
        var fresh = await PrepareAsync(f, batch.Id);
        Assert.Equal(0, fresh.Context.EligibleCoverage.EventCount);
        Assert.Equal(BatchReservationStatus.Closed, (await Batch(f).TryReserveBatchAttemptAsync(fresh, default)).Status);
        Assert.Empty(await f.Store.ReadPendingDeliveriesAsync(20, default));
    }

    [Fact]
    public async Task EquivalentRecipientOrderingReplaysCanonicalReservationFactsAfterReopen()
    {
        await using var f = await StoreFixture.CreateAsync();
        await f.AddAsync(StoreFixture.Key(), true, "A");
        await f.AddAsync(StoreFixture.Key(), true, "B");
        await f.ObserveAsync(StoreFixture.Key(), 119);
        var prepared = await PrepareAsync(f, (await ProjectAsync(f)).Id);
        var first = await Batch(f).TryReserveBatchAttemptAsync(prepared, default);
        var reordered = Context(prepared.Context, recipients: prepared.Context.EligibleRecipients.Reverse(), subscriptions: prepared.Context.Subscriptions.Reverse());
        var replay = new PreparedBatchAttempt(prepared.ReservationRequestId, reordered,
            BatchDeliveryPlanner.Plan(reordered, prepared.EvaluatedAtUtc), prepared.EvaluatedAtUtc);
        await f.ReopenAsync();
        var result = await Batch(f).TryReserveBatchAttemptAsync(replay, default);
        Assert.Equal(BatchReservationStatus.Replayed, result.Status);
        Assert.Equal(first.Attempt!.AttemptId, result.Attempt!.AttemptId);
        Assert.Equal(first.Attempt.Envelope.NativeDedupeKey, result.Attempt.Envelope.NativeDedupeKey);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResultReplayRejectsChangedTimeOrDiagnosticAndNeverChargesAgain(bool changeTime)
    {
        await using var f = await StoreFixture.CreateAsync();
        await TwoEventsAsync(f);
        var reserved = await ReserveAsync(f);
        var command = new BatchAttemptResultCommand(reserved.AttemptId, BatchAttemptResultOutcome.RetryableFailure, "desktop_error", f.Clock.GetUtcNow());
        var receipt = await Batch(f).RecordBatchAttemptResultAsync(command, default);
        await f.ReopenAsync();
        var sameInstant = new BatchAttemptResultCommand(command.AttemptId, command.Outcome, command.DiagnosticCode, command.RecordedAtUtc.ToOffset(TimeSpan.FromHours(9)));
        Assert.Equal(receipt, await Batch(f).RecordBatchAttemptResultAsync(sameInstant, default));
        var conflict = new BatchAttemptResultCommand(command.AttemptId, command.Outcome,
            changeTime ? command.DiagnosticCode : "different_error", changeTime ? command.RecordedAtUtc.AddTicks(1) : command.RecordedAtUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Batch(f).RecordBatchAttemptResultAsync(conflict, default));
        Assert.All(await f.Store.ReadPendingDeliveriesAsync(20, default), pending => Assert.Equal(1, pending.Attempts));
    }

    [Fact]
    public async Task ForgedCrossSectionContextCannotSealAndActualResultAcknowledgesOnlyItsOwnSection()
    {
        await using var f = await StoreFixture.CreateAsync();
        var first = StoreFixture.Key(); var other = StoreFixture.Key("013");
        await f.AddAsync(first); await f.AddAsync(other);
        var shared = await f.IssueAsync(first, other);
        await f.CommitAsync(f.Counts(shared, first, 119));
        await f.CommitAsync(f.Counts(shared, other, 119));
        var firstBatch = await ProjectAsync(f, first); var otherBatch = await ProjectAsync(f, other);
        var prepared = await PrepareAsync(f, firstBatch.Id);
        var foreign = (await PrepareAsync(f, otherBatch.Id)).Context;
        var wrongIdentity = new DeliveryBatchSnapshot(firstBatch.Id, other, foreign.Batch.Revision, foreign.Batch.Status,
            foreign.Batch.FoldCutoffOrdinal, foreign.Batch.FoldCursorOrdinal, foreign.Batch.FoldComplete, foreign.Batch.CreatedAtUtc);
        // The DTO remains internally coherent, but its authority names a real batch from another section.
        var forged = Context(foreign, batch: wrongIdentity);
        var attempt = new PreparedBatchAttempt(Guid.NewGuid(), forged, BatchDeliveryPlanner.Plan(forged, f.Clock.GetUtcNow()), f.Clock.GetUtcNow());
        Assert.Equal(BatchReservationStatus.VersionConflict, (await Batch(f).TryReserveBatchAttemptAsync(attempt, default)).Status);
        var reserved = await Batch(f).TryReserveBatchAttemptAsync(prepared, default);
        await Batch(f).RecordBatchAttemptResultAsync(new(reserved.Attempt!.AttemptId, BatchAttemptResultOutcome.OsAccepted, "os_accepted", f.Clock.GetUtcNow()), default);
        Assert.Equal(other, Assert.Single(await f.Store.ReadPendingDeliveriesAsync(20, default)).Key);
        Assert.Null((await PrepareAsync(f, otherBatch.Id)).Context.UnresolvedAttemptId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Batch(f).RecordBatchAttemptResultAsync(
            new(Guid.NewGuid(), BatchAttemptResultOutcome.OsAccepted, "os_accepted", f.Clock.GetUtcNow()), default));
        Assert.Single(await f.Store.ReadPendingDeliveriesAsync(20, default));
    }

    [Fact]
    public async Task ResolveWithoutSendFaultRollsBackEverySupersessionAndClosesOnlyOnRetry()
    {
        var armed = false; var members = 0;
        await using var f = await StoreFixture.CreateAsync(stage =>
        { if (armed && stage == SqliteStoreWriteStage.BatchMembers && ++members == 2) throw new EdgeFault(); });
        await TwoEventsAsync(f);
        await f.ObserveAsync(StoreFixture.Key(), 120);
        var batch = await ProjectAsync(f);
        var prepared = await PrepareAsync(f, batch.Id);
        Assert.Equal(BatchPlanDisposition.ResolveWithoutSend, prepared.Decision.Disposition);
        var before = await f.Store.ReadPendingDeliveriesAsync(20, default);
        armed = true;
        await Assert.ThrowsAsync<EdgeFault>(() => Batch(f).TryReserveBatchAttemptAsync(prepared, default));
        armed = false;
        await f.ReopenAsync();
        await AssertPendingUnchangedAsync(f, before);
        Assert.Null((await PrepareAsync(f, batch.Id)).Context.UnresolvedAttemptId);
        Assert.Equal(BatchReservationStatus.Closed, (await Batch(f).TryReserveBatchAttemptAsync(prepared, default)).Status);
        Assert.Empty(await f.Store.ReadPendingDeliveriesAsync(20, default));
        foreach (var original in before)
        {
            var resolved = (await f.Store.ReadDeliveryContextAsync(original.EventId, default)).Delivery;
            Assert.Equal(DeliveryStatus.Superseded, resolved.Status);
            Assert.Equal(0, resolved.Attempts);
        }
    }

    [Fact]
    public async Task AgingAfterAggregationWithoutAVersionChangeCannotAuthorizeStaleSend()
    {
        await using var f = await StoreFixture.CreateAsync();
        await f.AddAsync(StoreFixture.Key());
        await f.ObserveAsync(StoreFixture.Key(), 119);
        var batch = await ProjectAsync(f);
        var prepared = await PrepareAsync(f, batch.Id);
        Assert.Equal(BatchPlanDisposition.Send, prepared.Decision.Disposition);
        f.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(BatchReservationStatus.VersionConflict, (await Batch(f).TryReserveBatchAttemptAsync(prepared, default)).Status);
        var fresh = await PrepareAsync(f, batch.Id);
        Assert.Equal(prepared.Context.Section.Version, fresh.Context.Section.Version);
        Assert.Equal(prepared.Context.CatalogRevision, fresh.Context.CatalogRevision);
        Assert.Equal(BatchReservationStatus.NeedsFreshCheck, (await Batch(f).TryReserveBatchAttemptAsync(fresh, default)).Status);
        Assert.Null(fresh.Context.UnresolvedAttemptId);
        Assert.Equal(batch.Revision, fresh.Context.Batch.Revision);
        Assert.Equal(0, Assert.Single(await f.Store.ReadPendingDeliveriesAsync(20, default)).Attempts);
    }

    private static async Task TwoEventsAsync(StoreFixture f)
    {
        await f.AddAsync(StoreFixture.Key(), false);
        await f.ObserveAsync(StoreFixture.Key(), 120);
        await f.ObserveAsync(StoreFixture.Key(), 119);
        await f.ObserveAsync(StoreFixture.Key(), 116);
        Assert.Equal(2, (await f.Store.ReadPendingDeliveriesAsync(20, default)).Length);
    }

    private static async Task<DeliveryBatchSnapshot> ProjectAsync(StoreFixture f, SectionKey? key = null)
    {
        BatchProjection projection;
        do { projection = await Batch(f).ProjectDeliveryBatchAsync(new(key ?? StoreFixture.Key(), 20, f.Clock.GetUtcNow()), default); }
        while (projection.Status == BatchProjectionStatus.MoreToFold);
        return Assert.IsType<DeliveryBatchSnapshot>(projection.Batch);
    }

    private static async Task<PreparedBatchAttempt> PrepareAsync(StoreFixture f, Guid id)
    {
        var context = await Batch(f).ReadDeliveryBatchContextAsync(id, Policy, f.Clock.GetUtcNow(), default);
        return new(Guid.NewGuid(), context, BatchDeliveryPlanner.Plan(context, f.Clock.GetUtcNow()), f.Clock.GetUtcNow());
    }

    private static async Task<BatchAttemptSnapshot> ReserveAsync(StoreFixture f)
    {
        var result = await Batch(f).TryReserveBatchAttemptAsync(await PrepareAsync(f, (await ProjectAsync(f)).Id), default);
        Assert.Equal(BatchReservationStatus.Reserved, result.Status);
        return result.Attempt!;
    }

    private static BatchDeliveryContext Context(BatchDeliveryContext value, DeliveryBatchSnapshot? batch = null,
        IEnumerable<BatchRecipient>? recipients = null, IEnumerable<SubscriptionSnapshot>? subscriptions = null) =>
        new(batch ?? value.Batch, value.Section, value.CatalogRevision, value.Policy, value.EvaluatedAtUtc, value.ValidUntilUtc,
            value.PendingCount, value.IneligibleCount, value.BudgetExhaustedCount, value.EligibleCoverage, value.AllFactsDigest,
            recipients ?? value.EligibleRecipients, subscriptions ?? value.Subscriptions, value.UnresolvedAttemptId, value.RecipientLimitExceeded);

    private static async Task AssertPendingUnchangedAsync(StoreFixture f, IEnumerable<PendingDelivery> before)
    {
        var after = await f.Store.ReadPendingDeliveriesAsync(20, default);
        Assert.Equal(before.Select(value => value.EventId).Order(), after.Select(value => value.EventId).Order());
        foreach (var original in before)
        {
            var current = Assert.Single(after, value => value.EventId == original.EventId);
            Assert.Equal(original.DeliveryRevision, current.DeliveryRevision);
            Assert.Equal(original.Attempts, current.Attempts);
            Assert.Equal(original.Status, current.Status);
        }
    }

    private sealed class EdgeFault() : Exception("Injected independent edge-test failure.");
}
