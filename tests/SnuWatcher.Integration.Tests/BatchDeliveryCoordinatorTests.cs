using System.Collections.Immutable;
using SnuWatcher.Application.Delivery;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Observations;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Application.Watches;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Persistence;
using SnuWatcher.Integration.Tests.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests;

public sealed class BatchDeliveryCoordinatorTests
{
    private static readonly BatchDeliveryPolicy Policy = new(TimeSpan.FromMinutes(2), 3);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task TwoPageIncreaseAndInitialUnionSendsOnceAfterCompletedCutoffAndPreservesOriginalTriggers()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        var a = await fixture.AddAsync(key, initialNotice: false, label: "A");
        await fixture.ObserveAsync(key, 120);
        await fixture.ObserveAsync(key, 119);
        var b = await fixture.AddAsync(key, initialNotice: true, label: "B");
        await fixture.ObserveAsync(key, 119);
        var original = await fixture.Store.ReadPendingDeliveriesAsync(10, default);
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new Channel();
        var coordinator = new BatchDeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel);

        var firstPage = await coordinator.TryDeliverSectionAsync(key, Policy, pageSize: 1, maxFoldPages: 1);
        Assert.Equal(BatchDeliveryRunDisposition.MoreToFold, firstPage.Disposition);
        Assert.Empty(channel.Envelopes);
        var delivered = await coordinator.TryDeliverSectionAsync(key, Policy, pageSize: 1, maxFoldPages: 1);

        Assert.Equal(BatchDeliveryRunDisposition.ResultRecorded, delivered.Disposition);
        Assert.Equal(2, delivered.Receipt!.AcceptedCount);
        var envelope = Assert.Single(channel.Envelopes);
        Assert.Equal(2, envelope.Coverage.EventCount);
        Assert.Equal(new[] { a.Id, b.Id }.Order(), envelope.EligibleRecipients.Select(r => r.SubscriptionId).Order());
        Assert.Equal(new[] { OpeningReason.IncreasedSeats }, envelope.EligibleRecipients.Single(r => r.SubscriptionId == a.Id).Reasons);
        Assert.Equal(new[] { OpeningReason.InitialAvailability }, envelope.EligibleRecipients.Single(r => r.SubscriptionId == b.Id).Reasons);
        Assert.Equal(firstPage.BatchId!.Value.ToString("N"), envelope.NativeDedupeKey);
        Assert.Empty(await fixture.Store.ReadPendingDeliveriesAsync(10, default));
        foreach (var historical in original)
        {
            var accepted = (await fixture.Store.ReadDeliveryContextAsync(historical.EventId, default)).Delivery;
            Assert.Equal(DeliveryStatus.OsAccepted, accepted.Status);
            Assert.Equal(historical.Trigger.AttemptSequence, accepted.Trigger.AttemptSequence);
            Assert.Equal(historical.Trigger.Recipients, accepted.Trigger.Recipients);
        }
    }

    [Fact]
    public async Task FailedDesktopWithSuccessfulAuditSurvivesReopenAndRetriesFewerCurrentSeatsUsingSameBatchKey()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        await fixture.AddAsync(key);
        await fixture.ObserveAsync(key, 116);
        var original = Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(10, default));
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new Channel { Handler = (_, _) => Task.FromResult(BatchChannelOutcome.RetryableFailure) };
        List<BatchDeliveryAudit> audits = [];
        var failed = await new BatchDeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel, audits.Add)
            .TryDeliverSectionAsync(key, Policy);
        Assert.Equal(BatchChannelOutcome.RetryableFailure, failed.ChannelOutcome);
        Assert.Equal(1, Assert.Single(audits).Result.Receipt!.PendingCount);
        await fixture.ReopenAsync();
        Assert.Equal(1, Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(10, default)).Attempts);
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.ObserveAsync(key, 118);
        channel.Handler = (_, _) => Task.FromResult(BatchChannelOutcome.OsAccepted);

        var retry = await new BatchDeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel).TryDeliverSectionAsync(key, Policy);

        Assert.Equal(1, retry.Receipt!.AcceptedCount);
        Assert.Equal(2, channel.Envelopes.Count);
        Assert.Equal(channel.Envelopes[0].NativeDedupeKey, channel.Envelopes[1].NativeDedupeKey);
        Assert.NotEqual(channel.Envelopes[0].AttemptId, channel.Envelopes[1].AttemptId);
        Assert.Equal(2, channel.Envelopes[1].CurrentSeats);
        Assert.Equal(fixture.Clock.GetUtcNow(), channel.Envelopes[1].CurrentSuccessAtUtc);
        var accepted = (await fixture.Store.ReadDeliveryContextAsync(original.EventId, default)).Delivery;
        Assert.Equal(4, accepted.Trigger.CurrentSeats);
        Assert.Equal(original.Trigger.ObservedAt, accepted.Trigger.ObservedAt);
        Assert.Equal(2, accepted.Attempts);
    }

    [Theory]
    [InlineData("full", BatchDeliveryRunDisposition.Closed)]
    [InlineData("paused", BatchDeliveryRunDisposition.Closed)]
    [InlineData("expired", BatchDeliveryRunDisposition.Closed)]
    [InlineData("unknown", BatchDeliveryRunDisposition.NeedsFreshCheck)]
    public async Task CurrentFullInactiveOrUnknownStateNeverSendsOldOpening(string change, BatchDeliveryRunDisposition expected)
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        var watch = await fixture.AddAsync(key);
        await fixture.ObserveAsync(key, 119);
        var original = Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(10, default));
        if (change == "full") await fixture.ObserveAsync(key, 120);
        if (change == "paused") await fixture.Store.ChangeSubscriptionAsync(new PauseSubscription(watch.Id,
            watch.DefinitionRevision, fixture.Clock.GetUtcNow()), default);
        if (change == "expired")
        {
            await fixture.Store.ChangeSubscriptionAsync(new SetSubscriptionExpiry(watch.Id, watch.DefinitionRevision,
                fixture.Clock.GetUtcNow(), WatchExpiry.FromLocalDate(new DateOnly(2026, 11, 1), TimeZoneInfo.Utc)), default);
            fixture.Clock.Advance(TimeSpan.FromDays(1));
        }
        if (change == "unknown")
        {
            var issued = await fixture.IssueAsync(key);
            await fixture.CommitAsync(fixture.Unknown(issued, key));
        }
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new Channel();

        var result = await new BatchDeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel).TryDeliverSectionAsync(key, Policy);

        Assert.Equal(expected, result.Disposition);
        Assert.Empty(channel.Envelopes);
        var state = (await fixture.Store.ReadDeliveryContextAsync(original.EventId, default)).Delivery;
        Assert.Equal(change == "unknown" ? DeliveryStatus.Pending : DeliveryStatus.Superseded, state.Status);
        Assert.Equal(0, state.Attempts);
    }

    [Fact]
    public async Task PendingDriverDoesNotHoldSqlTransactionAndNewPollIsNotAcknowledgedWhilePauseWaitsForRecordedResult()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        var other = StoreFixture.Key("013");
        var watch = await fixture.AddAsync(key);
        await fixture.AddAsync(other);
        await fixture.ObserveAsync(other, 120);
        await fixture.ObserveAsync(key, 119);
        var original = Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(10, default));
        using var gate = new SemaphoreSlim(1, 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<BatchChannelOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new Channel { Handler = (_, _) => { started.SetResult(); return finish.Task; } };
        var coordinator = new BatchDeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel);
        var sending = coordinator.TryDeliverSectionAsync(key, Policy);
        await started.Task.WaitAsync(Timeout);
        var pause = new WatchManagementService(fixture.Store, gate).ChangeAsync(new PauseSubscription(
            watch.Id, watch.DefinitionRevision, fixture.Clock.GetUtcNow()));
        Assert.False(pause.IsCompleted);
        Guid newerEvent;
        try
        {
            await fixture.ObserveAsync(key, 118).WaitAsync(Timeout);
            newerEvent = (await fixture.Store.ReadPendingDeliveriesAsync(10, default)).Single(e => e.EventId != original.EventId).EventId;
            // A second store/connection must obtain a write reservation while the actual driver awaits.
            await using var writer = await SqliteWatchStore.OpenAsync(new SqliteConnectionFactory(fixture.DatabasePath), fixture.Clock);
            var issued = await writer.IssueCheckAsync(new(fixture.Clock.GetUtcNow(), [other]), default).WaitAsync(Timeout);
            var context = await writer.ReadCommitContextAsync(issued.Token, other, default).WaitAsync(Timeout);
            var prepared = ObservationPreparer.Prepare(context, fixture.Counts(issued, other, 119), fixture.Clock.GetUtcNow());
            Assert.Equal(CommitWriteStatus.Committed, (await writer.TryCommitSectionAsync(prepared, default).WaitAsync(Timeout)).Status);
            Assert.False(pause.IsCompleted);
        }
        finally { finish.TrySetResult(BatchChannelOutcome.OsAccepted); }
        var delivered = await sending.WaitAsync(Timeout);
        await pause.WaitAsync(Timeout);
        Assert.Equal(1, delivered.Receipt!.AcceptedCount);
        Assert.Equal(DeliveryStatus.OsAccepted, (await fixture.Store.ReadDeliveryContextAsync(original.EventId, default)).Delivery.Status);
        var unclaimed = (await fixture.Store.ReadDeliveryContextAsync(newerEvent, default)).Delivery;
        Assert.Equal(DeliveryStatus.Pending, unclaimed.Status);
        Assert.Equal(0, unclaimed.Attempts);
        var skipped = await coordinator.TryDeliverSectionAsync(key, Policy);
        Assert.Equal(BatchDeliveryRunDisposition.Closed, skipped.Disposition);
        Assert.Single(channel.Envelopes);
    }

    [Fact]
    public async Task AtomicResultWriteFaultKeepsKnownAcceptanceAndRetriesOriginalCommandDespiteAgedSource()
    {
        var armed = false;
        await using var fixture = await StoreFixture.CreateAsync(stage =>
        {
            if (armed && stage == SqliteStoreWriteStage.BatchResult) throw new InvalidOperationException("private store fault");
        });
        var key = StoreFixture.Key();
        await fixture.AddAsync(key);
        await fixture.ObserveAsync(key, 119);
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new Channel();
        var coordinator = new BatchDeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel);
        armed = true;
        var first = await coordinator.TryDeliverSectionAsync(key, Policy);
        armed = false;
        Assert.Equal(BatchChannelOutcome.OsAccepted, first.ChannelOutcome);
        Assert.Equal(BatchDeliveryRunDisposition.ResultNotRecorded, first.Disposition);
        Assert.Equal(0, Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(10, default)).Attempts);
        fixture.Clock.Advance(TimeSpan.FromHours(1));

        var flush = await coordinator.TryDeliverSectionAsync(key, Policy);

        Assert.Same(first.KnownResult, flush.KnownResult);
        Assert.Equal(first.KnownResult!.RecordedAtUtc, flush.Receipt!.RecordedAtUtc);
        Assert.Equal(1, flush.Receipt.AcceptedCount);
        Assert.False(flush.SubmittedThisCall);
        Assert.Single(channel.Envelopes);
        await fixture.ReopenAsync();
        Assert.Empty(await fixture.Store.ReadPendingDeliveriesAsync(10, default));
        Assert.Equal(flush.Receipt, await fixture.Store.RecordBatchAttemptResultAsync(first.KnownResult, default));
    }

    [Fact]
    public async Task LostCommittedReturnIsDiscoverableOutsidePendingWorkPageAndFlushFreesCapacityWithoutResending()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        await fixture.AddAsync(key);
        await fixture.ObserveAsync(key, 119);
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new Channel();
        var port = new LoseFirstResultReturn(fixture.Store);
        var coordinator = new BatchDeliveryCoordinator(port, fixture.Clock, gate, channel, maxPendingKnownResults: 1);
        var first = await coordinator.TryDeliverSectionAsync(key, Policy);
        Assert.Equal(BatchDeliveryRunDisposition.ResultNotRecorded, first.Disposition);
        Assert.Empty(await fixture.Store.ReadPendingDeliverySectionsAsync(1, default));
        var other = StoreFixture.Key("013");
        await fixture.AddAsync(other);
        await fixture.ObserveAsync(other, 119);
        var listed = Assert.Single(await fixture.Store.ReadPendingDeliverySectionsAsync(1, default));
        Assert.Equal(other, listed.Key);
        Assert.Equal(BatchDeliveryRunDisposition.PendingResultLimit,
            (await coordinator.TryDeliverSectionAsync(other, Policy)).Disposition);
        var retained = Assert.Single(await coordinator.ReadPendingKnownSectionsAsync());
        Assert.Equal(key, retained);
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));

        var flush = await coordinator.TryDeliverSectionAsync(retained, Policy);

        Assert.Same(first.KnownResult, flush.KnownResult);
        Assert.Equal(first.KnownResult!.RecordedAtUtc, flush.Receipt!.RecordedAtUtc);
        Assert.False(flush.SubmittedThisCall);
        Assert.Empty(await coordinator.ReadPendingKnownSectionsAsync());
        Assert.Single(channel.Envelopes);
        Assert.Equal(1, (await coordinator.TryDeliverSectionAsync(other, Policy)).Receipt!.AcceptedCount);
        Assert.Equal(2, channel.Envelopes.Count);
        Assert.Equal(port.Commands[0], port.Commands[1]);
    }

    [Fact]
    public async Task CancellationDuringReservedTransactionReturnsNotSubmittedWithoutChargingAttempt()
    {
        using var cancellation = new CancellationTokenSource();
        await using var fixture = await StoreFixture.CreateAsync(stage =>
        {
            if (stage == SqliteStoreWriteStage.BatchReserved) cancellation.Cancel();
        });
        var key = StoreFixture.Key();
        await fixture.AddAsync(key);
        await fixture.ObserveAsync(key, 119);
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new Channel();

        var result = await new BatchDeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel)
            .TryDeliverSectionAsync(key, Policy, cancellationToken: cancellation.Token);

        Assert.Equal(BatchAttemptResultOutcome.NotSubmitted, result.KnownResult!.Outcome);
        Assert.False(result.SubmittedThisCall);
        Assert.Empty(channel.Envelopes);
        await fixture.ReopenAsync();
        Assert.Equal(0, Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(10, default)).Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterInvocationPreservesKnownAcceptanceOrConservativeUnknown(bool known)
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        await fixture.AddAsync(key);
        await fixture.ObserveAsync(key, 119);
        var original = Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(10, default));
        using var gate = new SemaphoreSlim(1, 1);
        using var cancellation = new CancellationTokenSource();
        var channel = new Channel { Handler = (_, token) =>
        {
            cancellation.Cancel();
            return known ? Task.FromResult(BatchChannelOutcome.OsAccepted) : Task.FromCanceled<BatchChannelOutcome>(token);
        } };

        var result = await new BatchDeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel)
            .TryDeliverSectionAsync(key, Policy, cancellationToken: cancellation.Token);

        Assert.Equal(known ? BatchAttemptResultOutcome.OsAccepted : BatchAttemptResultOutcome.InterruptedUnknown, result.KnownResult!.Outcome);
        await fixture.ReopenAsync();
        var persisted = (await fixture.Store.ReadDeliveryContextAsync(original.EventId, default)).Delivery;
        Assert.Equal(known ? DeliveryStatus.OsAccepted : DeliveryStatus.Pending, persisted.Status);
        Assert.Equal(1, persisted.Attempts);
    }

    [Fact]
    public async Task RestartUnresolvedAttemptBlocksWithoutAutomaticRecoveryOrSubmission()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        await fixture.AddAsync(key);
        await fixture.ObserveAsync(key, 119);
        var unresolved = await StoreBatchBehaviorTests.ReserveAsync(fixture, key);
        await fixture.ReopenAsync();
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new Channel();

        var result = await new BatchDeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel).TryDeliverSectionAsync(key, Policy);

        Assert.Equal(BatchDeliveryRunDisposition.NeedsRecovery, result.Disposition);
        Assert.Equal(unresolved.AttemptId, result.AttemptId);
        Assert.Empty(channel.Envelopes);
        Assert.Equal(0, Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(10, default)).Attempts);
    }

    [Fact]
    public async Task ConcurrentSameSectionCallsProduceOneAcceptedSubmissionAndSecondFindsNoWork()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        await fixture.AddAsync(key);
        await fixture.ObserveAsync(key, 119);
        using var gate = new SemaphoreSlim(1, 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<BatchChannelOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new Channel { Handler = (_, _) => { started.SetResult(); return finish.Task; } };
        var coordinator = new BatchDeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel);
        var first = coordinator.TryDeliverSectionAsync(key, Policy);
        await started.Task.WaitAsync(Timeout);
        var second = coordinator.TryDeliverSectionAsync(key, Policy);
        Assert.False(second.IsCompleted);
        finish.SetResult(BatchChannelOutcome.OsAccepted);

        Assert.Equal(1, (await first.WaitAsync(Timeout)).Receipt!.AcceptedCount);
        Assert.Equal(BatchDeliveryRunDisposition.NoWork, (await second.WaitAsync(Timeout)).Disposition);
        Assert.Single(channel.Envelopes);
    }

    [Theory]
    [InlineData(BatchChannelReadiness.Initializing)]
    [InlineData(BatchChannelReadiness.Disabled)]
    [InlineData(BatchChannelReadiness.Unavailable)]
    public async Task BlockedNativeReadinessCreatesNoAttemptAndDoesNotHoldSqliteFromNewObservations(BatchChannelReadiness readiness)
    {
        await using var f = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        await f.AddAsync(key); await f.ObserveAsync(key, 119);
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new Channel { Readiness = _ => Task.FromResult(readiness) };
        var result = await new BatchDeliveryCoordinator(f.Store, f.Clock, gate, channel).TryDeliverSectionAsync(key, Policy);
        Assert.Equal(BatchDeliveryRunDisposition.ChannelDeferred, result.Disposition);
        Assert.Equal(0, CountRows(f, "SELECT count(*) FROM DeliveryBatchAttempts;"));
        Assert.Equal(0, CountRows(f, "SELECT count(*) FROM DeliveryBatchResults;"));
        await f.ObserveAsync(key, 118);
        Assert.Equal(2, (await f.Store.ReadPendingDeliveriesAsync(10, default)).Length);
        Assert.All(await f.Store.ReadPendingDeliveriesAsync(10, default), item => Assert.Equal(0, item.Attempts));
        Assert.Empty(channel.Envelopes);
    }

    [Fact]
    public async Task ReadyThenDisabledAfterSealRecordsZeroCostProofAndRetryKeepsBatchKeyAcrossReopen()
    {
        var ready = true;
        await using var f = await StoreFixture.CreateAsync(stage => { if (stage == SqliteStoreWriteStage.BatchReserved) ready = false; });
        var key = StoreFixture.Key();
        await f.AddAsync(key); await f.ObserveAsync(key, 119);
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new Channel
        {
            Readiness = _ => Task.FromResult(ready ? BatchChannelReadiness.Ready : BatchChannelReadiness.Disabled),
            SendResult = (_, _) => Task.FromResult(new BatchChannelResult(BatchChannelOutcome.NotSubmitted, BatchNoSubmissionReason.ChannelNotReady)),
        };
        var first = await new BatchDeliveryCoordinator(f.Store, f.Clock, gate, channel).TryDeliverSectionAsync(key, Policy);
        Assert.False(first.SubmittedThisCall);
        Assert.Equal(BatchAttemptResultOutcome.NotSubmitted, first.Receipt!.Outcome);
        Assert.Equal(1, CountRows(f, "SELECT count(*) FROM DeliveryBatchAttempts WHERE status=1;"));
        Assert.Equal(1, CountRows(f, "SELECT count(*) FROM DeliveryBatchResults;"));
        await f.ReopenAsync();
        Assert.Equal(0, Assert.Single(await f.Store.ReadPendingDeliveriesAsync(10, default)).Attempts);
        Assert.Empty(await f.Store.ReadUnresolvedAttemptsAsync(null, 10));
        ready = true;
        channel.SendResult = null;
        var retry = await new BatchDeliveryCoordinator(f.Store, f.Clock, gate, channel).TryDeliverSectionAsync(key, Policy);
        Assert.Equal(first.BatchId, retry.BatchId);
        Assert.Equal(channel.Envelopes[0].NativeDedupeKey, channel.Envelopes[1].NativeDedupeKey);
        Assert.NotEqual(first.AttemptId, retry.AttemptId);
        Assert.Equal(1, retry.Receipt!.AcceptedCount);
    }

    [Fact]
    public async Task LostZeroCostResultReturnIsReplayedExactlyWhileNativeReadinessIsBlocked()
    {
        await using var f = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        await f.AddAsync(key); await f.ObserveAsync(key, 119);
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new Channel { SendResult = (_, _) => Task.FromResult(new BatchChannelResult(
            BatchChannelOutcome.NotSubmitted, BatchNoSubmissionReason.PayloadRejected)) };
        var port = new LoseFirstResultReturn(f.Store);
        var coordinator = new BatchDeliveryCoordinator(port, f.Clock, gate, channel);
        var first = await coordinator.TryDeliverSectionAsync(key, Policy);
        channel.Readiness = _ => throw new InvalidOperationException("known result flush must not probe");
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        var replay = await coordinator.TryDeliverSectionAsync(key, Policy);
        Assert.Same(first.KnownResult, replay.KnownResult);
        Assert.Equal(first.KnownResult!.RecordedAtUtc, replay.Receipt!.RecordedAtUtc);
        Assert.False(replay.SubmittedThisCall);
        Assert.Single(channel.Envelopes);
        Assert.Equal(1, CountRows(f, "SELECT count(*) FROM DeliveryBatchResults;"));
        await f.ReopenAsync();
        Assert.Equal(0, Assert.Single(await f.Store.ReadPendingDeliveriesAsync(10, default)).Attempts);
    }

    [Fact]
    public async Task CrossingMinimumRecipientExpiryRefusesWholeUnionThenRetryKeepsStillActiveRecipient()
    {
        await using var f = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        var expiry = WatchExpiry.FromLocalDate(new DateOnly(2026, 11, 1), TimeZoneInfo.Utc);
        f.Clock.Set(expiry.ExclusiveUtc.AddSeconds(-1));
        var expiring = await f.Store.CreateSubscriptionAsync(f.NewSection(key, expiry: expiry), default);
        var steady = await f.AddAsync(key);
        await f.ObserveAsync(key, 119);
        var original = Assert.Single(await f.Store.ReadPendingDeliveriesAsync(10, default));
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new Channel { SendResult = (submission, _) =>
        {
            Assert.Equal(expiry.ExclusiveUtc, submission.EligibilityExpiresAtUtc);
            Assert.Equal(2, submission.Envelope.EligibleRecipients.Length);
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            return Task.FromResult(new BatchChannelResult(BatchChannelOutcome.NotSubmitted, BatchNoSubmissionReason.ValidityChanged));
        } };
        var coordinator = new BatchDeliveryCoordinator(f.Store, f.Clock, gate, channel);
        var refused = await coordinator.TryDeliverSectionAsync(key, Policy);
        Assert.Equal(BatchAttemptResultOutcome.NotSubmitted, refused.Receipt!.Outcome);
        Assert.Equal(0, Assert.Single(await f.Store.ReadPendingDeliveriesAsync(10, default)).Attempts);
        channel.SendResult = null;
        var delivered = await coordinator.TryDeliverSectionAsync(key, Policy);
        Assert.Equal(refused.BatchId, delivered.BatchId);
        Assert.Equal(steady.Id, Assert.Single(channel.Envelopes[1].EligibleRecipients).SubscriptionId);
        Assert.DoesNotContain(channel.Envelopes[1].EligibleRecipients, recipient => recipient.SubscriptionId == expiring.Id);
        var persisted = (await f.Store.ReadDeliveryContextAsync(original.EventId, default)).Delivery;
        Assert.Equal(2, persisted.Trigger.Recipients.Count);
        Assert.Equal(1, persisted.Attempts);
        Assert.Equal(DeliveryStatus.OsAccepted, persisted.Status);
    }

    [Fact]
    public async Task CrashWithUnresolvedSealStillCostsOneConservativeAttemptEvenWithoutAnyKnownOsCall()
    {
        await using var f = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        await f.AddAsync(key); await f.ObserveAsync(key, 119);
        await StoreBatchBehaviorTests.ReserveAsync(f, key);
        await f.ReopenAsync();
        using var gate = new SemaphoreSlim(1, 1);
        var recovered = await new DeliveryStartupRecovery(f.Store, f.Store, f.Clock, gate).RecoverAsync();
        Assert.Equal(1, recovered.RecoveredAttempts);
        Assert.Equal(1, Assert.Single(await f.Store.ReadPendingDeliveriesAsync(10, default)).Attempts);
        Assert.Empty(await f.Store.ReadUnresolvedAttemptsAsync(null, 10));
    }

    private static long CountRows(StoreFixture fixture, string fixedSql)
    {
        using var connection = new SqliteConnectionFactory(fixture.DatabasePath).Open();
        using var command = connection.CreateCommand();
        command.CommandText = fixedSql;
        return (long)command.ExecuteScalar()!;
    }

    private sealed class Channel : IBatchDesktopAlertChannel
    {
        internal List<BatchDeliveryEnvelope> Envelopes { get; } = [];
        internal Func<BatchDeliveryEnvelope, CancellationToken, Task<BatchChannelOutcome>> Handler { get; set; } = (_, _) =>
            Task.FromResult(BatchChannelOutcome.OsAccepted);
        internal Func<BatchDesktopSubmission, CancellationToken, Task<BatchChannelResult>>? SendResult { get; set; }
        public Func<CancellationToken, Task<BatchChannelReadiness>> Readiness { get; set; } = _ => Task.FromResult(BatchChannelReadiness.Ready);
        public Task<BatchChannelReadiness> ReadReadinessAsync(CancellationToken token) => Readiness(token);
        public async Task<BatchChannelResult> SendAsync(BatchDesktopSubmission submission, CancellationToken cancellationToken)
        {
            var envelope = submission.Envelope;
            Envelopes.Add(envelope);
            return SendResult is { } scripted ? await scripted(submission, cancellationToken)
                : new BatchChannelResult(await Handler(envelope, cancellationToken));
        }
    }

    /// <summary>Actual SQLite commits; only the first completed write's return is deliberately lost.</summary>
    private sealed class LoseFirstResultReturn(IBatchDeliveryStore inner) : IBatchDeliveryStore
    {
        private bool _lost;
        internal List<BatchAttemptResultCommand> Commands { get; } = [];
        public Task<ImmutableArray<SectionDeliveryWork>> ReadPendingDeliverySectionsAsync(int limit, CancellationToken ct) => inner.ReadPendingDeliverySectionsAsync(limit, ct);
        public Task<BatchProjection> ProjectDeliveryBatchAsync(ProjectDeliveryBatchCommand command, CancellationToken ct) => inner.ProjectDeliveryBatchAsync(command, ct);
        public Task<BatchDeliveryContext> ReadDeliveryBatchContextAsync(Guid id, BatchDeliveryPolicy policy, DateTimeOffset at, CancellationToken ct) => inner.ReadDeliveryBatchContextAsync(id, policy, at, ct);
        public Task<BatchReservationResult> TryReserveBatchAttemptAsync(PreparedBatchAttempt command, CancellationToken ct) => inner.TryReserveBatchAttemptAsync(command, ct);
        public async Task<BatchAttemptReceipt> RecordBatchAttemptResultAsync(BatchAttemptResultCommand command, CancellationToken ct)
        {
            Commands.Add(command);
            var receipt = await inner.RecordBatchAttemptResultAsync(command, ct);
            if (!_lost) { _lost = true; throw new IOException("Simulated lost return after committed result."); }
            return receipt;
        }
        public Task<ImmutableArray<BatchAttemptCoverageItem>> ReadBatchAttemptCoverageAsync(Guid id, long after, int limit, CancellationToken ct) => inner.ReadBatchAttemptCoverageAsync(id, after, limit, ct);
    }
}
