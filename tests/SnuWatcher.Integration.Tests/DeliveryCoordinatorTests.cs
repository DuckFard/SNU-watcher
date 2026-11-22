using SnuWatcher.Application.Delivery;
using SnuWatcher.Application.Observations;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Persistence;
using SnuWatcher.Integration.Tests.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests;

public sealed class DeliveryCoordinatorTests
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SuccessfulAuditCannotAcknowledgeFailedDesktopAndPendingSurvivesReopen()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var pending = await OpeningAsync(fixture);
        using var gate = new SemaphoreSlim(1, 1);
        List<DeliveryAttemptAudit> audits = [];
        var channel = new TestChannel((_, _) => Task.FromResult(DesktopAlertOutcome.RetryableFailure));
        var coordinator = new DeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel, audits.Add);

        var result = await coordinator.TryDeliverAsync(pending.EventId, MaxAge, 3, default);
        await fixture.ReopenAsync();

        Assert.Equal(DesktopAlertOutcome.RetryableFailure, result.ChannelOutcome);
        Assert.Equal(DeliveryWriteResult.Applied, result.WriteResult);
        var audit = Assert.Single(audits);
        Assert.Equal(pending.EventId, audit.EventId);
        Assert.Equal(1, audit.Attempts);
        Assert.Equal(DesktopAlertOutcome.RetryableFailure, audit.ChannelOutcome);
        Assert.Equal(DeliveryWriteResult.Applied, audit.WriteResult);
        Assert.Equal("desktop_retryable_failure", audit.ReasonCode);
        Assert.Equal(fixture.Clock.GetUtcNow(), audit.EvaluatedAtUtc);
        Assert.False(result.ObserverFailed);
        var preserved = (await fixture.Store.ReadDeliveryContextAsync(pending.EventId, default)).Delivery;
        Assert.Equal(DeliveryStatus.Pending, preserved.Status);
        Assert.Equal(1, preserved.Attempts);
        Assert.Equal(pending.EventId, Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(10, default)).EventId);
    }

    [Fact]
    public async Task ObserverRunsOutsideGateAndItsFailureCannotMaskOsAccepted()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var pending = await OpeningAsync(fixture);
        using var gate = new SemaphoreSlim(1, 1);
        var observerCalled = false;
        var observerEnteredUnlockedGate = false;
        var channel = new TestChannel((_, _) => Task.FromResult(DesktopAlertOutcome.OsAccepted));
        var coordinator = new DeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel, _ =>
        {
            observerCalled = true;
            if (gate.Wait(0))
            {
                observerEnteredUnlockedGate = true;
                gate.Release();
            }
            throw new InvalidOperationException("private audit detail");
        });

        var result = await coordinator.TryDeliverAsync(pending.EventId, MaxAge, 3, default);

        Assert.True(observerCalled);
        Assert.True(observerEnteredUnlockedGate);
        Assert.True(result.ObserverFailed);
        Assert.Equal(DesktopAlertOutcome.OsAccepted, result.ChannelOutcome);
        Assert.Equal(DeliveryWriteResult.Applied, result.WriteResult);
        Assert.Equal("os_accepted", result.ReasonCode);
        Assert.Equal(DeliveryStatus.OsAccepted,
            (await fixture.Store.ReadDeliveryContextAsync(pending.EventId, default)).Delivery.Status);
    }

    [Fact]
    public async Task RetryAfterReopenUsesFewerCurrentSeatsAndSameNativeDedupeIdentity()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var pending = await OpeningAsync(fixture);
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new TestChannel((_, _) => Task.FromResult(DesktopAlertOutcome.RetryableFailure));
        await new DeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel)
            .TryDeliverAsync(pending.EventId, MaxAge, 3, default);
        await fixture.ReopenAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.ObserveAsync(StoreFixture.Key(), 118);
        channel.Handler = (_, _) => Task.FromResult(DesktopAlertOutcome.OsAccepted);

        var result = await new DeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel)
            .TryDeliverAsync(pending.EventId, MaxAge, 3, default);

        Assert.Equal(DesktopAlertOutcome.OsAccepted, result.ChannelOutcome);
        Assert.Equal(2, channel.Envelopes.Count);
        Assert.All(channel.Envelopes, envelope => Assert.Equal(pending.NativeDedupeKey, envelope.NativeDedupeKey));
        var retry = channel.Envelopes[1];
        Assert.Equal(2, retry.CurrentSeats);
        Assert.Equal(4, retry.Trigger.CurrentSeats);
        Assert.Equal(fixture.Clock.GetUtcNow(), retry.CurrentSuccessAtUtc);
        Assert.Equal(pending.Trigger.ObservedAt, retry.Trigger.ObservedAt);
        Assert.Equal(2, (await fixture.Store.ReadDeliveryContextAsync(pending.EventId, default)).Delivery.Attempts);
    }

    [Theory]
    [InlineData("full")]
    [InlineData("paused")]
    [InlineData("removed")]
    [InlineData("resumed")]
    [InlineData("expired")]
    public async Task NoLongerEligibleEventIsPersistedAsSupersededWithoutCallingDesktop(string change)
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var pending = await OpeningAsync(fixture);
        var watch = Assert.Single(await fixture.Store.ListSubscriptionsAsync(default));
        if (change == "full") await fixture.ObserveAsync(StoreFixture.Key(), 120);
        if (change is "paused" or "resumed")
        {
            watch = await fixture.Store.ChangeSubscriptionAsync(
                new PauseSubscription(watch.Id, watch.DefinitionRevision, fixture.Clock.GetUtcNow()), default);
            if (change == "resumed")
                await fixture.Store.ChangeSubscriptionAsync(
                    new ResumeSubscription(watch.Id, watch.DefinitionRevision, fixture.Clock.GetUtcNow()), default);
        }
        if (change == "removed")
            await fixture.Store.ChangeSubscriptionAsync(
                new RemoveSubscription(watch.Id, watch.DefinitionRevision, fixture.Clock.GetUtcNow()), default);
        if (change == "expired")
        {
            await fixture.Store.ChangeSubscriptionAsync(new SetSubscriptionExpiry(watch.Id, watch.DefinitionRevision,
                fixture.Clock.GetUtcNow(), WatchExpiry.FromLocalDate(new DateOnly(2026, 11, 1), TimeZoneInfo.Utc)), default);
            fixture.Clock.Advance(TimeSpan.FromDays(1));
        }
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new TestChannel((_, _) => Task.FromResult(DesktopAlertOutcome.OsAccepted));

        var result = await new DeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel)
            .TryDeliverAsync(pending.EventId, MaxAge, 3, default);

        Assert.Equal(DeliveryDisposition.Superseded, result.Decision.Disposition);
        Assert.Null(result.ChannelOutcome);
        Assert.Equal(DeliveryWriteResult.Applied, result.WriteResult);
        Assert.Empty(channel.Envelopes);
        await fixture.ReopenAsync();
        var superseded = (await fixture.Store.ReadDeliveryContextAsync(pending.EventId, default)).Delivery;
        Assert.Equal(DeliveryStatus.Superseded, superseded.Status);
        Assert.Equal(0, superseded.Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownOrAgedSourceWaitsWithoutSendingOrAdvancingAttempts(bool unknown)
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var pending = await OpeningAsync(fixture);
        if (unknown)
        {
            var issued = await fixture.IssueAsync(StoreFixture.Key());
            await fixture.CommitAsync(fixture.Unknown(issued, StoreFixture.Key()));
        }
        else fixture.Clock.Advance(MaxAge.Add(TimeSpan.FromTicks(1)));
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new TestChannel((_, _) => Task.FromResult(DesktopAlertOutcome.OsAccepted));

        var result = await new DeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel)
            .TryDeliverAsync(pending.EventId, MaxAge, 3, default);

        Assert.Equal(DeliveryDisposition.NeedsFreshCheck, result.Decision.Disposition);
        Assert.Null(result.ChannelOutcome);
        Assert.Null(result.WriteResult);
        Assert.Empty(channel.Envelopes);
        var unchanged = (await fixture.Store.ReadDeliveryContextAsync(pending.EventId, default)).Delivery;
        Assert.Equal(pending.DeliveryRevision, unchanged.DeliveryRevision);
        Assert.Equal(0, unchanged.Attempts);
    }

    [Theory]
    [InlineData("failure", "desktop_retryable_failure")]
    [InlineData("throw", "desktop_error")]
    [InlineData("internal_cancel", "desktop_error")]
    [InlineData("invalid", "invalid_desktop_result")]
    public async Task FailureRetriesUntilBoundThenBecomesTerminalWithSanitizedReason(string mode, string reason)
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var pending = await OpeningAsync(fixture);
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new TestChannel((_, _) => mode switch
        {
            "throw" => throw new InvalidOperationException("secret desktop internals"),
            "internal_cancel" => throw new OperationCanceledException("secret internal timeout"),
            "invalid" => Task.FromResult((DesktopAlertOutcome)999),
            _ => Task.FromResult(DesktopAlertOutcome.RetryableFailure),
        });
        var coordinator = new DeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel);

        var first = await coordinator.TryDeliverAsync(pending.EventId, MaxAge, 2, default);
        Assert.Equal(reason, first.ReasonCode);
        Assert.Equal(DesktopAlertOutcome.RetryableFailure, first.ChannelOutcome);
        Assert.Equal(DeliveryStatus.Pending, (await fixture.Store.ReadDeliveryContextAsync(pending.EventId, default)).Delivery.Status);
        var second = await coordinator.TryDeliverAsync(pending.EventId, MaxAge, 2, default);
        Assert.Equal(DeliveryWriteResult.Applied, second.WriteResult);
        await fixture.ReopenAsync();
        var failed = (await fixture.Store.ReadDeliveryContextAsync(pending.EventId, default)).Delivery;
        Assert.Equal(DeliveryStatus.Failed, failed.Status);
        Assert.Equal(2, failed.Attempts);
        var terminal = await new DeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel)
            .TryDeliverAsync(pending.EventId, MaxAge, 2, default);
        Assert.Equal(DeliveryDisposition.Terminal, terminal.Decision.Disposition);
        Assert.Null(terminal.ChannelOutcome);
        Assert.Equal(2, channel.Envelopes.Count);
    }

    [Fact]
    public async Task AlreadyExhaustedPendingDoesNotOverflowOrCallChannel()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var pending = await OpeningAsync(fixture);
        await fixture.Store.RecordDeliveryResultAsync(new(pending.EventId, pending.DeliveryRevision,
            DeliveryStatus.Pending, int.MaxValue, fixture.Clock.GetUtcNow()), default);
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new TestChannel((_, _) => Task.FromResult(DesktopAlertOutcome.OsAccepted));

        var result = await new DeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel)
            .TryDeliverAsync(pending.EventId, MaxAge, 100, default);

        Assert.Equal("attempt_limit", result.ReasonCode);
        Assert.Null(result.ChannelOutcome);
        Assert.Empty(channel.Envelopes);
        var failed = (await fixture.Store.ReadDeliveryContextAsync(pending.EventId, default)).Delivery;
        Assert.Equal(DeliveryStatus.Failed, failed.Status);
        Assert.Equal(int.MaxValue, failed.Attempts);
    }

    [Fact]
    public async Task SameEventConcurrentDeliverySendsOnceAndPollingWritesContinueDuringPendingChannel()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var pending = await OpeningAsync(fixture);
        using var gate = new SemaphoreSlim(1, 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<DesktopAlertOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new TestChannel((_, _) => { started.SetResult(); return release.Task; });
        var coordinator = new DeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel);
        var first = coordinator.TryDeliverAsync(pending.EventId, MaxAge, 3, default);
        await started.Task.WaitAsync(Timeout);
        var second = coordinator.TryDeliverAsync(pending.EventId, MaxAge, 3, default);
        try
        {
            Assert.False(second.IsCompleted);
            Assert.False(gate.Wait(0));
            var other = StoreFixture.Key("013");
            await fixture.AddAsync(other).WaitAsync(Timeout);
            await fixture.ObserveAsync(other, 120).WaitAsync(Timeout);
            // A separate lane/connection must also write while OS delivery awaits.
            await using var writer = await SqliteWatchStore.OpenAsync(new SqliteConnectionFactory(fixture.DatabasePath), fixture.Clock);
            var issued = await writer.IssueCheckAsync(new(fixture.Clock.GetUtcNow(), [other]), default).WaitAsync(Timeout);
            var context = await writer.ReadCommitContextAsync(issued.Token, other, default).WaitAsync(Timeout);
            var prepared = ObservationPreparer.Prepare(context, fixture.Counts(issued, other, 119), fixture.Clock.GetUtcNow());
            Assert.Equal(CommitWriteStatus.Committed,
                (await writer.TryCommitSectionAsync(prepared, default).WaitAsync(Timeout)).Status);
        }
        finally { release.TrySetResult(DesktopAlertOutcome.OsAccepted); }

        Assert.Equal(DesktopAlertOutcome.OsAccepted, (await first.WaitAsync(Timeout)).ChannelOutcome);
        Assert.Equal(DeliveryDisposition.Terminal, (await second.WaitAsync(Timeout)).Decision.Disposition);
        Assert.Single(channel.Envelopes);
    }

    [Fact]
    public async Task SharedLifecycleGateDoesNotCompletePauseUntilCurrentSendIsRecorded()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var pending = await OpeningAsync(fixture);
        var watch = Assert.Single(await fixture.Store.ListSubscriptionsAsync(default));
        using var gate = new SemaphoreSlim(1, 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<DesktopAlertOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new TestChannel((_, _) => { started.SetResult(); return release.Task; });
        var coordinator = new DeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel);
        var delivery = coordinator.TryDeliverAsync(pending.EventId, MaxAge, 3, default);
        await started.Task.WaitAsync(Timeout);
        var pause = PauseUnderGateAsync();
        Assert.False(pause.IsCompleted);
        release.SetResult(DesktopAlertOutcome.RetryableFailure);
        await Task.WhenAll(delivery, pause).WaitAsync(Timeout);

        var retry = await coordinator.TryDeliverAsync(pending.EventId, MaxAge, 3, default);
        Assert.Equal(DeliveryDisposition.Superseded, retry.Decision.Disposition);
        Assert.Single(channel.Envelopes);

        async Task PauseUnderGateAsync()
        {
            await gate.WaitAsync();
            try
            {
                Assert.Equal(1, (await fixture.Store.ReadDeliveryContextAsync(pending.EventId, default)).Delivery.Attempts);
                await fixture.Store.ChangeSubscriptionAsync(new PauseSubscription(watch.Id, watch.DefinitionRevision,
                    fixture.Clock.GetUtcNow()), default);
            }
            finally { gate.Release(); }
        }
    }

    [Fact]
    public async Task CancellationWhileWaitingForGateDoesNotSendOrChangeDelivery()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var pending = await OpeningAsync(fixture);
        using var gate = new SemaphoreSlim(0, 1);
        using var cancellation = new CancellationTokenSource();
        var channel = new TestChannel((_, _) => Task.FromResult(DesktopAlertOutcome.OsAccepted));
        var attempt = new DeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel)
            .TryDeliverAsync(pending.EventId, MaxAge, 3, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
        Assert.Empty(channel.Envelopes);
        AssertUnchanged(pending, (await fixture.Store.ReadDeliveryContextAsync(pending.EventId, default)).Delivery);
    }

    [Fact]
    public async Task CancellationAfterReadBeforeChannelDoesNotSendAndReleasesGate()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var pending = await OpeningAsync(fixture);
        using var gate = new SemaphoreSlim(1, 1);
        using var cancellation = new CancellationTokenSource();
        var clock = new CallbackClock(() => { cancellation.Cancel(); return fixture.Clock.GetUtcNow(); });
        var channel = new TestChannel((_, _) => Task.FromResult(DesktopAlertOutcome.OsAccepted));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DeliveryCoordinator(fixture.Store, clock, gate, channel)
                .TryDeliverAsync(pending.EventId, MaxAge, 3, cancellation.Token));

        Assert.Empty(channel.Envelopes);
        AssertUnchanged(pending, (await fixture.Store.ReadDeliveryContextAsync(pending.EventId, default)).Delivery);
        Assert.True(gate.Wait(0));
        gate.Release();
    }

    [Fact]
    public async Task CallerCanceledChannelWithoutAuthoritativeReturnLeavesPendingUnchanged()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var pending = await OpeningAsync(fixture);
        using var gate = new SemaphoreSlim(1, 1);
        using var cancellation = new CancellationTokenSource();
        var channel = new TestChannel((_, token) => { cancellation.Cancel(); return Task.FromCanceled<DesktopAlertOutcome>(token); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel)
                .TryDeliverAsync(pending.EventId, MaxAge, 3, cancellation.Token));

        Assert.Single(channel.Envelopes);
        AssertUnchanged(pending, (await fixture.Store.ReadDeliveryContextAsync(pending.EventId, default)).Delivery);
    }

    [Theory]
    [InlineData(DesktopAlertOutcome.OsAccepted, DeliveryStatus.OsAccepted)]
    [InlineData(DesktopAlertOutcome.RetryableFailure, DeliveryStatus.Pending)]
    public async Task AuthoritativeChannelResultIsRecordedDespiteLateCallerCancellation(DesktopAlertOutcome outcome, DeliveryStatus status)
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var pending = await OpeningAsync(fixture);
        using var gate = new SemaphoreSlim(1, 1);
        using var cancellation = new CancellationTokenSource();
        var channel = new TestChannel((_, _) => { cancellation.Cancel(); return Task.FromResult(outcome); });

        var result = await new DeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel)
            .TryDeliverAsync(pending.EventId, MaxAge, 3, cancellation.Token);
        await fixture.ReopenAsync();

        Assert.Equal(outcome, result.ChannelOutcome);
        Assert.Equal(DeliveryWriteResult.Applied, result.WriteResult);
        var saved = (await fixture.Store.ReadDeliveryContextAsync(pending.EventId, default)).Delivery;
        Assert.Equal(status, saved.Status);
        Assert.Equal(1, saved.Attempts);
    }

    [Fact]
    public async Task AcceptedDesktopButFailedResultWriteRetainsBothFactsAndRetriesStableKeyAfterReopen()
    {
        var armed = false;
        await using var fixture = await StoreFixture.CreateAsync(stage =>
        {
            if (armed && stage == SqliteStoreWriteStage.Delivery) throw new InvalidOperationException("private database internals");
        });
        var pending = await OpeningAsync(fixture);
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new TestChannel((_, _) => Task.FromResult(DesktopAlertOutcome.OsAccepted));
        armed = true;

        var result = await new DeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel)
            .TryDeliverAsync(pending.EventId, MaxAge, 3, default);
        armed = false;
        await fixture.ReopenAsync();

        Assert.Equal(DesktopAlertOutcome.OsAccepted, result.ChannelOutcome);
        Assert.Null(result.WriteResult);
        Assert.Equal("result_not_recorded", result.ReasonCode);
        var preserved = (await fixture.Store.ReadDeliveryContextAsync(pending.EventId, default)).Delivery;
        Assert.Equal(DeliveryStatus.Pending, preserved.Status);
        Assert.Equal(0, preserved.Attempts);
        await new DeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel)
            .TryDeliverAsync(pending.EventId, MaxAge, 3, default);
        Assert.Equal(2, channel.Envelopes.Count);
        Assert.All(channel.Envelopes, envelope => Assert.Equal(pending.NativeDedupeKey, envelope.NativeDedupeKey));
    }

    [Fact]
    public async Task AcceptedChannelAndCasConflictRemainSeparateFacts()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var pending = await OpeningAsync(fixture);
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new TestChannel(async (_, _) =>
        {
            await fixture.Store.RecordDeliveryResultAsync(new(pending.EventId, pending.DeliveryRevision,
                DeliveryStatus.Pending, 1, fixture.Clock.GetUtcNow()), default);
            return DesktopAlertOutcome.OsAccepted;
        });

        var result = await new DeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel)
            .TryDeliverAsync(pending.EventId, MaxAge, 3, default);

        Assert.Equal(DesktopAlertOutcome.OsAccepted, result.ChannelOutcome);
        Assert.Equal(DeliveryWriteResult.VersionConflict, result.WriteResult);
        Assert.Equal("result_version_conflict", result.ReasonCode);
        Assert.Equal(DeliveryStatus.Pending, (await fixture.Store.ReadDeliveryContextAsync(pending.EventId, default)).Delivery.Status);
    }

    [Fact]
    public async Task MissingEventReleasesGateAndDoesNotSend()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new TestChannel((_, _) => Task.FromResult(DesktopAlertOutcome.OsAccepted));
        var coordinator = new DeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => coordinator.TryDeliverAsync(Guid.NewGuid(), MaxAge, 3, default));
        var pending = await OpeningAsync(fixture);

        Assert.Equal(DesktopAlertOutcome.OsAccepted,
            (await coordinator.TryDeliverAsync(pending.EventId, MaxAge, 3, default).WaitAsync(Timeout)).ChannelOutcome);
        Assert.Single(channel.Envelopes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task InvalidAttemptBoundIsRejectedBeforeSending(int maxAttempts)
    {
        await using var fixture = await StoreFixture.CreateAsync();
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new TestChannel((_, _) => Task.FromResult(DesktopAlertOutcome.OsAccepted));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new DeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel)
                .TryDeliverAsync(Guid.NewGuid(), MaxAge, maxAttempts, default));
        Assert.Empty(channel.Envelopes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(86401)]
    public async Task InvalidFreshnessBoundIsRejectedBeforeReadingDelivery(int seconds)
    {
        await using var fixture = await StoreFixture.CreateAsync();
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new TestChannel((_, _) => Task.FromResult(DesktopAlertOutcome.OsAccepted));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new DeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel)
                .TryDeliverAsync(Guid.NewGuid(), TimeSpan.FromSeconds(seconds), 3));
        Assert.Empty(channel.Envelopes);
    }

    [Fact]
    public async Task EmptyIdentityIsRejectedBeforeStoreRead()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        using var gate = new SemaphoreSlim(1, 1);
        var channel = new TestChannel((_, _) => Task.FromResult(DesktopAlertOutcome.OsAccepted));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new DeliveryCoordinator(fixture.Store, fixture.Clock, gate, channel).TryDeliverAsync(Guid.Empty, MaxAge, 3));
        Assert.Empty(channel.Envelopes);
    }

    [Fact]
    public async Task CancellationDuringSupersededWriteDoesNotInventDeliveryAcknowledgement()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var pending = await OpeningAsync(fixture);
        var watch = Assert.Single(await fixture.Store.ListSubscriptionsAsync(default));
        await fixture.Store.ChangeSubscriptionAsync(new PauseSubscription(watch.Id, watch.DefinitionRevision,
            fixture.Clock.GetUtcNow()), default);
        using var gate = new SemaphoreSlim(1, 1);
        using var cancellation = new CancellationTokenSource();
        var clockReads = 0;
        var clock = new CallbackClock(() =>
        {
            if (++clockReads == 2) cancellation.Cancel();
            return fixture.Clock.GetUtcNow();
        });
        var channel = new TestChannel((_, _) => Task.FromResult(DesktopAlertOutcome.OsAccepted));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DeliveryCoordinator(fixture.Store, clock, gate, channel)
                .TryDeliverAsync(pending.EventId, MaxAge, 3, cancellation.Token));

        Assert.Empty(channel.Envelopes);
        AssertUnchanged(pending, (await fixture.Store.ReadDeliveryContextAsync(pending.EventId, default)).Delivery);
    }

    private static void AssertUnchanged(PendingDelivery before, PendingDelivery after)
    {
        Assert.Equal(before.EventId, after.EventId);
        Assert.Equal(before.DeliveryRevision, after.DeliveryRevision);
        Assert.Equal(before.Attempts, after.Attempts);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.CreatedAtUtc, after.CreatedAtUtc);
        Assert.Equal(before.Trigger.Key, after.Trigger.Key);
        Assert.Equal(before.Trigger.CurrentSeats, after.Trigger.CurrentSeats);
        Assert.Equal(before.Trigger.AttemptSequence, after.Trigger.AttemptSequence);
        Assert.Equal(before.Trigger.Recipients, after.Trigger.Recipients);
    }

    private static async Task<PendingDelivery> OpeningAsync(StoreFixture fixture)
    {
        await fixture.AddAsync(StoreFixture.Key());
        await fixture.ObserveAsync(StoreFixture.Key(), 116);
        return Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(10, default));
    }

    private sealed class TestChannel(Func<DeliveryEnvelope, CancellationToken, Task<DesktopAlertOutcome>> handler) : IDesktopAlertChannel
    {
        public Func<DeliveryEnvelope, CancellationToken, Task<DesktopAlertOutcome>> Handler { get; set; } = handler;
        public List<DeliveryEnvelope> Envelopes { get; } = [];
        public Task<DesktopAlertOutcome> SendAsync(DeliveryEnvelope envelope, CancellationToken cancellationToken)
        {
            Envelopes.Add(envelope);
            return Handler(envelope, cancellationToken);
        }
    }

    private sealed class CallbackClock(Func<DateTimeOffset> read) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => read();
    }
}
