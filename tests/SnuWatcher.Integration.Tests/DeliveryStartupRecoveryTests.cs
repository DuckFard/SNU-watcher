using System.Collections.Immutable;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Persistence;
using SnuWatcher.Integration.Tests.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests;

public sealed class DeliveryStartupRecoveryTests
{
    [Fact]
    public async Task DirectDiscoveryFindsAllUnresolvedPagesBuriedBehindOlderOrdinaryPendingWork()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        for (var section = 10; section < 15; section++)
        {
            var ordinary = StoreFixture.Key(section.ToString("000"));
            await fixture.AddAsync(ordinary);
            await fixture.ObserveAsync(ordinary, 119);
        }
        List<BatchAttemptSnapshot> unresolved = [];
        for (var section = 20; section < 25; section++)
            unresolved.Add(await ReserveAsync(fixture, StoreFixture.Key(section.ToString("000"))));
        var hiddenByWorkPage = await fixture.Store.ReadPendingDeliverySectionsAsync(2, default);
        Assert.All(hiddenByWorkPage, work => Assert.False(work.HasUnresolvedAttempt));
        var before = await fixture.Store.ReadUnresolvedAttemptsAsync(null, 1000, default);
        Assert.Equal(5, before.Length);
        Assert.Equal(before.Select(row => row.AttemptId.ToString("N")).Order(StringComparer.Ordinal),
            before.Select(row => row.AttemptId.ToString("N")));
        Assert.All(before, row =>
        {
            Assert.Equal(TimeSpan.Zero, row.PreparedAtUtc.Offset);
            Assert.Equal(unresolved.Single(attempt => attempt.AttemptId == row.AttemptId).Envelope.Key, row.Key);
        });
        using var gate = new SemaphoreSlim(1, 1);

        var summary = await new DeliveryStartupRecovery(fixture.Store, fixture.Store, fixture.Clock, gate).RecoverAsync(pageSize: 2);

        Assert.Equal(5, summary.RecoveredAttempts);
        Assert.Equal(5, summary.CoveredEvents);
        Assert.Empty(await fixture.Store.ReadUnresolvedAttemptsAsync(null, 1000, default));
        var allPending = await fixture.Store.ReadPendingDeliveriesAsync(20, default);
        Assert.Equal(10, allPending.Length);
        Assert.All(allPending.Where(row => int.Parse(row.Key.Section) < 20), row => Assert.Equal(0, row.Attempts));
        Assert.All(allPending.Where(row => int.Parse(row.Key.Section) >= 20), row => Assert.Equal(1, row.Attempts));
        await fixture.ReopenAsync();
        var next = await StoreBatchBehaviorTests.ReserveAsync(fixture, unresolved[0].Envelope.Key);
        Assert.Equal(unresolved[0].Envelope.NativeDedupeKey, next.Envelope.NativeDedupeKey);
        Assert.NotEqual(unresolved[0].AttemptId, next.AttemptId);
    }

    [Fact]
    public async Task RecoveryChargesOnlyFrozenCoverageAndPreservesNewerEventsBaselineAndHealth()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        await fixture.AddAsync(key);
        await fixture.ObserveAsync(key, 119);
        await fixture.AddAsync(key, initialNotice: true, label: "B initial");
        await fixture.ObserveAsync(key, 118);
        var reserved = await StoreBatchBehaviorTests.ReserveAsync(fixture, key);
        var covered = await fixture.Store.ReadBatchAttemptCoverageAsync(reserved.AttemptId, 0, 200, default);
        Assert.Equal(2, covered.Length);
        await fixture.ObserveAsync(key, 117);
        var before = await fixture.Store.ReadSectionAsync(key, default);
        using var gate = new SemaphoreSlim(1, 1);

        var summary = await new DeliveryStartupRecovery(fixture.Store, fixture.Store, fixture.Clock, gate).RecoverAsync(1);

        Assert.Equal(1, summary.RecoveredAttempts);
        Assert.Equal(2, summary.CoveredEvents);
        var pending = await fixture.Store.ReadPendingDeliveriesAsync(20, default);
        Assert.Equal(3, pending.Length);
        Assert.All(pending.Where(row => covered.Any(item => item.EventId == row.EventId)), row => Assert.Equal(1, row.Attempts));
        Assert.Equal(0, Assert.Single(pending.Where(row => covered.All(item => item.EventId != row.EventId))).Attempts);
        var after = await fixture.Store.ReadSectionAsync(key, default);
        Assert.Equal(before.Baseline, after.Baseline);
        Assert.Equal(before.LastSuccessfulAtUtc, after.LastSuccessfulAtUtc);
        Assert.Equal(before.LatestOutcome, after.LatestOutcome);
        Assert.Equal(before.LatestCompletedOrdinal, after.LatestCompletedOrdinal);
    }

    [Fact]
    public async Task AcceptedReceiptNeverAppearsInRecoveryOrConsumesAnotherBudgetUnit()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var accepted = await ReserveAsync(fixture, StoreFixture.Key("010"));
        var acceptedCoverage = Assert.Single(await fixture.Store.ReadBatchAttemptCoverageAsync(accepted.AttemptId, 0, 20, default));
        var command = new BatchAttemptResultCommand(accepted.AttemptId, BatchAttemptResultOutcome.OsAccepted,
            "os_accepted", fixture.Clock.GetUtcNow());
        var originalReceipt = await fixture.Store.RecordBatchAttemptResultAsync(command, default);
        var unresolved = await ReserveAsync(fixture, StoreFixture.Key("020"));
        Assert.Equal(unresolved.AttemptId, Assert.Single(await fixture.Store.ReadUnresolvedAttemptsAsync(null, 20, default)).AttemptId);
        using var gate = new SemaphoreSlim(1, 1);

        Assert.Equal(1, (await new DeliveryStartupRecovery(fixture.Store, fixture.Store, fixture.Clock, gate).RecoverAsync()).RecoveredAttempts);
        await fixture.ReopenAsync();

        Assert.Equal(originalReceipt, await fixture.Store.RecordBatchAttemptResultAsync(command, default));
        var historical = (await fixture.Store.ReadDeliveryContextAsync(acceptedCoverage.EventId, default)).Delivery;
        Assert.Equal(DeliveryStatus.OsAccepted, historical.Status);
        Assert.Equal(1, historical.Attempts);
        Assert.Empty(await fixture.Store.ReadUnresolvedAttemptsAsync(null, 20, default));
    }

    [Fact]
    public async Task PersistentWriteFaultRollsBackAndAbortsStartupThenFreshInstanceRecoversAfterReopen()
    {
        var armed = false;
        await using var fixture = await StoreFixture.CreateAsync(stage =>
        {
            if (armed && stage == SqliteStoreWriteStage.BatchResult) throw new IOException("private database fault");
        });
        var original = await ReserveAsync(fixture, StoreFixture.Key());
        using var gate = new SemaphoreSlim(1, 1);
        var recovery = new DeliveryStartupRecovery(fixture.Store, fixture.Store, fixture.Clock, gate);
        armed = true;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.RecoverAsync());

        Assert.DoesNotContain("private", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.Single(await fixture.Store.ReadUnresolvedAttemptsAsync(null, 20, default));
        Assert.Equal(0, Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(20, default)).Attempts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.RecoverAsync());
        armed = false;
        await fixture.ReopenAsync();
        var summary = await new DeliveryStartupRecovery(fixture.Store, fixture.Store, fixture.Clock, gate).RecoverAsync();
        Assert.Equal(1, summary.RecoveredAttempts);
        Assert.Equal(1, Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(20, default)).Attempts);
        var retry = await StoreBatchBehaviorTests.ReserveAsync(fixture, StoreFixture.Key());
        Assert.Equal(original.Envelope.NativeDedupeKey, retry.Envelope.NativeDedupeKey);
    }

    [Fact]
    public async Task CommittedButLostRecoveryReturnReplaysIdenticalCommandAndChargesOnceEvenWithLateCancellation()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await ReserveAsync(fixture, StoreFixture.Key());
        using var gate = new SemaphoreSlim(1, 1);
        using var cancellation = new CancellationTokenSource();
        var port = new LoseRecoveryReturn(fixture.Store, () =>
        {
            fixture.Clock.Advance(TimeSpan.FromHours(1));
            cancellation.Cancel();
        });

        var summary = await new DeliveryStartupRecovery(fixture.Store, port, fixture.Clock, gate)
            .RecoverAsync(cancellationToken: cancellation.Token);

        Assert.Equal(1, summary.RecoveredAttempts);
        Assert.Equal(2, port.Commands.Count);
        Assert.Same(port.Commands[0], port.Commands[1]);
        Assert.Equal(1, Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(20, default)).Attempts);
        Assert.Empty(await fixture.Store.ReadUnresolvedAttemptsAsync(null, 20, default));
        await fixture.ReopenAsync();
        var receipt = await fixture.Store.RecordBatchAttemptResultAsync(port.Commands[0], default);
        Assert.Equal(port.Commands[0].RecordedAtUtc, receipt.RecordedAtUtc);
        Assert.Equal(1, receipt.PendingCount);
    }

    [Fact]
    public async Task CancellationDuringOneUncancelledRecordLeavesPartialDurableRecoveryAndNoDuplicateOnRestart()
    {
        using var cancellation = new CancellationTokenSource();
        var armed = false;
        await using var fixture = await StoreFixture.CreateAsync(stage =>
        {
            if (armed && stage == SqliteStoreWriteStage.BatchResult) cancellation.Cancel();
        });
        for (var section = 10; section < 13; section++) await ReserveAsync(fixture, StoreFixture.Key(section.ToString("000")));
        using var gate = new SemaphoreSlim(1, 1);
        armed = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DeliveryStartupRecovery(fixture.Store, fixture.Store, fixture.Clock, gate).RecoverAsync(2, cancellation.Token));

        Assert.Equal(2, (await fixture.Store.ReadUnresolvedAttemptsAsync(null, 20, default)).Length);
        Assert.Single((await fixture.Store.ReadPendingDeliveriesAsync(20, default)).Where(row => row.Attempts == 1));
        armed = false;
        await fixture.ReopenAsync();
        Assert.Equal(2, (await new DeliveryStartupRecovery(fixture.Store, fixture.Store, fixture.Clock, gate).RecoverAsync(1)).RecoveredAttempts);
        Assert.All(await fixture.Store.ReadPendingDeliveriesAsync(20, default), row => Assert.Equal(1, row.Attempts));
    }

    [Fact]
    public async Task DiscoveryCursorIsExclusiveCanonicalImmutableAndBoundsRejectBeforeQuerying()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await ReserveAsync(fixture, StoreFixture.Key("010"));
        await ReserveAsync(fixture, StoreFixture.Key("020"));
        var first = await fixture.Store.ReadUnresolvedAttemptsAsync(null, 1, default);
        var second = await fixture.Store.ReadUnresolvedAttemptsAsync(first[0].AttemptId, 1, default);
        Assert.Single(first);
        Assert.Single(second);
        Assert.True(StringComparer.Ordinal.Compare(first[0].AttemptId.ToString("N"), second[0].AttemptId.ToString("N")) < 0);
        Assert.Empty(await fixture.Store.ReadUnresolvedAttemptsAsync(second[0].AttemptId, 1, default));
        Assert.Throws<NotSupportedException>(() => ((IList<UnresolvedDeliveryAttempt>)first).Clear());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Store.ReadUnresolvedAttemptsAsync(null, 0, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Store.ReadUnresolvedAttemptsAsync(null, 1001, default));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.ReadUnresolvedAttemptsAsync(Guid.Empty, 1, default));
    }

    [Fact]
    public async Task InterruptedRecoveryUsesEachFrozenMembersOwnBudgetAndRetainsBatchKeyForNewerMember()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        var first = await ReserveAsync(fixture, key);
        var oldEvent = Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(20, default));
        await fixture.Store.RecordBatchAttemptResultAsync(new(first.AttemptId, BatchAttemptResultOutcome.RetryableFailure,
            "desktop_retryable_failure", fixture.Clock.GetUtcNow()), default);
        var second = await StoreBatchBehaviorTests.ReserveAsync(fixture, key);
        await fixture.Store.RecordBatchAttemptResultAsync(new(second.AttemptId, BatchAttemptResultOutcome.RetryableFailure,
            "desktop_retryable_failure", fixture.Clock.GetUtcNow()), default);
        var newerWatch = await fixture.AddAsync(key, initialNotice: true, label: "Newer member");
        await fixture.ObserveAsync(key, 119);
        var interrupted = await StoreBatchBehaviorTests.ReserveAsync(fixture, key);
        Assert.Equal(2, interrupted.Envelope.Coverage.EventCount);
        using var gate = new SemaphoreSlim(1, 1);

        var summary = await new DeliveryStartupRecovery(fixture.Store, fixture.Store, fixture.Clock, gate).RecoverAsync();

        Assert.Equal(2, summary.CoveredEvents);
        var failed = (await fixture.Store.ReadDeliveryContextAsync(oldEvent.EventId, default)).Delivery;
        Assert.Equal(DeliveryStatus.Failed, failed.Status);
        Assert.Equal(3, failed.Attempts);
        var remaining = Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(20, default));
        Assert.Equal(1, remaining.Attempts);
        Assert.Equal(newerWatch.Id.ToString("N"), Assert.Single(remaining.Trigger.Recipients).SubscriptionId);
        var next = await StoreBatchBehaviorTests.ReserveAsync(fixture, key);
        Assert.Equal(first.Envelope.NativeDedupeKey, next.Envelope.NativeDedupeKey);
        Assert.Equal(newerWatch.Id, Assert.Single(next.Envelope.EligibleRecipients).SubscriptionId);
    }

    [Fact]
    public async Task ContradictoryClosedBatchWithUnresolvedReservationAbortsStartupWithoutWritingResults()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var unresolved = await ReserveAsync(fixture, StoreFixture.Key());
        // Corrupt only this fixture's data, leaving the producer schema unchanged.
        ExecuteFixtureData(fixture, "UPDATE DeliveryBatches SET status=$closed WHERE id=$id;",
            ("$closed", (int)DeliveryBatchStatus.Closed), ("$id", unresolved.Envelope.BatchId.ToString("N")));
        using var gate = new SemaphoreSlim(1, 1);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DeliveryStartupRecovery(fixture.Store, fixture.Store, fixture.Clock, gate).RecoverAsync());

        Assert.Contains("discovery_failed", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.Equal(0, Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(10, default)).Attempts);
    }

    [Fact]
    public async Task ForeignImmutableAttemptSnapshotCannotGrantRecoveryAuthorityForAnotherRow()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var first = await ReserveAsync(fixture, StoreFixture.Key("010"));
        var second = await ReserveAsync(fixture, StoreFixture.Key("020"));
        ExecuteFixtureData(fixture, """
            UPDATE DeliveryBatchAttempts SET snapshot=(SELECT snapshot FROM DeliveryBatchAttempts WHERE id=$source)
            WHERE id=$target;
            """, ("$source", second.AttemptId.ToString("N")), ("$target", first.AttemptId.ToString("N")));
        using var gate = new SemaphoreSlim(1, 1);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DeliveryStartupRecovery(fixture.Store, fixture.Store, fixture.Clock, gate).RecoverAsync());

        Assert.Contains("discovery_failed", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.All(await fixture.Store.ReadPendingDeliveriesAsync(10, default), row => Assert.Equal(0, row.Attempts));
    }

    private static void ExecuteFixtureData(StoreFixture fixture, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnectionFactory(fixture.DatabasePath).Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        command.ExecuteNonQuery();
    }

    private static async Task<BatchAttemptSnapshot> ReserveAsync(StoreFixture fixture, SectionKey key)
    {
        await fixture.AddAsync(key);
        await fixture.ObserveAsync(key, 119);
        return await StoreBatchBehaviorTests.ReserveAsync(fixture, key);
    }

    private sealed class LoseRecoveryReturn(IBatchDeliveryStore inner, Action afterCommit) : IBatchDeliveryStore
    {
        internal List<BatchAttemptResultCommand> Commands { get; } = [];
        public Task<ImmutableArray<SectionDeliveryWork>> ReadPendingDeliverySectionsAsync(int limit, CancellationToken ct) => inner.ReadPendingDeliverySectionsAsync(limit, ct);
        public Task<BatchProjection> ProjectDeliveryBatchAsync(ProjectDeliveryBatchCommand command, CancellationToken ct) => inner.ProjectDeliveryBatchAsync(command, ct);
        public Task<BatchDeliveryContext> ReadDeliveryBatchContextAsync(Guid id, BatchDeliveryPolicy policy, DateTimeOffset at, CancellationToken ct) => inner.ReadDeliveryBatchContextAsync(id, policy, at, ct);
        public Task<BatchReservationResult> TryReserveBatchAttemptAsync(PreparedBatchAttempt command, CancellationToken ct) => inner.TryReserveBatchAttemptAsync(command, ct);
        public async Task<BatchAttemptReceipt> RecordBatchAttemptResultAsync(BatchAttemptResultCommand command, CancellationToken ct)
        {
            Assert.False(ct.CanBeCanceled);
            Commands.Add(command);
            var receipt = await inner.RecordBatchAttemptResultAsync(command, ct);
            if (Commands.Count == 1) { afterCommit(); throw new IOException("Simulated lost committed recovery return."); }
            return receipt;
        }
        public Task<ImmutableArray<BatchAttemptCoverageItem>> ReadBatchAttemptCoverageAsync(Guid id, long after, int limit, CancellationToken ct) => inner.ReadBatchAttemptCoverageAsync(id, after, limit, ct);
    }
}
