using SnuWatcher.Application.Observations;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using SnuWatcher.Infrastructure.Persistence;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreBehaviorTests
{
    [Fact]
    public async Task IssuanceAndActivationWatermarkSurviveReopenAndExcludeInflightInitialization()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        var inflight = await fixture.IssueAsync(key);
        var watch = await fixture.AddAsync(key);
        Assert.Equal(inflight.Token.Ordinal, Assert.Single(watch.Members).ActivationWatermark);

        var oldCompletion = await fixture.CommitAsync(fixture.Counts(inflight, key, 119));
        Assert.Null(oldCompletion.Receipt!.EventId);
        Assert.Null(Assert.Single((await fixture.Store.FindSubscriptionAsync(watch.Id, default))!.Members).InitializedByAttemptOrdinal);

        await fixture.ReopenAsync();
        var fresh = await fixture.IssueAsync(key);
        Assert.Equal(inflight.Token.Ordinal + 1, fresh.Token.Ordinal);
        var result = await fixture.CommitAsync(fixture.Counts(fresh, key, 119));
        Assert.NotNull(result.Receipt!.EventId);
        Assert.Equal(fresh.Token.Ordinal,
            Assert.Single((await fixture.Store.FindSubscriptionAsync(watch.Id, default))!.Members).InitializedByAttemptOrdinal);
    }

    [Fact]
    public async Task CreatingTheSameIdentityIsIdempotentAndConflictingPayloadDoesNotOverwriteIt()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var command = fixture.NewSection(StoreFixture.Key());
        var first = await fixture.Store.CreateSubscriptionAsync(command, default);
        var replay = await fixture.Store.CreateSubscriptionAsync(command, default);
        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(first.DefinitionRevision, replay.DefinitionRevision);
        Assert.Single(await fixture.Store.ListSubscriptionsAsync(default));

        var changed = new NewSubscription(command.Id, "Different label", command.Kind, command.Sections,
            command.InitialNoticeEnabled, command.Expiry, command.CreatedAtUtc);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.Store.CreateSubscriptionAsync(changed, default));
        Assert.Equal(command.Label, (await fixture.Store.FindSubscriptionAsync(command.Id, default))!.Label);
    }

    [Fact]
    public async Task RenameAndExpiryKeepInitializationWhileResumeCreatesANewActivationGeneration()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        var watch = await fixture.AddAsync(key, initialNotice: false);
        var initialized = await fixture.ObserveAsync(key, 119);
        watch = (await fixture.Store.FindSubscriptionAsync(watch.Id, default))!;
        var ordinal = Assert.Single(watch.Members).InitializedByAttemptOrdinal;
        Assert.NotNull(ordinal);

        watch = await fixture.Store.ChangeSubscriptionAsync(new RenameSubscription(watch.Id, watch.DefinitionRevision,
            fixture.Clock.GetUtcNow(), "Any statistics class"), default);
        var expiry = WatchExpiry.FromLocalDate(new DateOnly(2026, 11, 3));
        watch = await fixture.Store.ChangeSubscriptionAsync(new SetSubscriptionExpiry(watch.Id, watch.DefinitionRevision,
            fixture.Clock.GetUtcNow(), expiry), default);
        Assert.Equal(1, watch.ActivationGeneration);
        Assert.Equal(ordinal, Assert.Single(watch.Members).InitializedByAttemptOrdinal);

        watch = await fixture.Store.ChangeSubscriptionAsync(new PauseSubscription(watch.Id, watch.DefinitionRevision,
            fixture.Clock.GetUtcNow()), default);
        var inflight = await fixture.IssueAsync(key);
        watch = await fixture.Store.ChangeSubscriptionAsync(new ResumeSubscription(watch.Id, watch.DefinitionRevision,
            fixture.Clock.GetUtcNow()), default);
        Assert.Equal(2, watch.ActivationGeneration);
        Assert.Equal(inflight.Token.Ordinal, Assert.Single(watch.Members).ActivationWatermark);
        Assert.Null(Assert.Single(watch.Members).InitializedByAttemptOrdinal);
        Assert.Equal("Any statistics class", watch.Label);
        Assert.Equal(expiry, watch.Expiry);

        await fixture.CommitAsync(fixture.Counts(inflight, key, 118));
        Assert.Null(Assert.Single((await fixture.Store.FindSubscriptionAsync(watch.Id, default))!.Members).InitializedByAttemptOrdinal);
        var fresh = await fixture.IssueAsync(key);
        var result = await fixture.CommitAsync(fixture.Counts(fresh, key, 118));
        Assert.Null(result.Receipt!.EventId); // New generation with initial notices disabled.
        Assert.Equal(fresh.Token.Ordinal,
            Assert.Single((await fixture.Store.FindSubscriptionAsync(watch.Id, default))!.Members).InitializedByAttemptOrdinal);
        Assert.Empty(await fixture.Store.ReadPendingDeliveriesAsync(20, default));
        Assert.Null(initialized.Receipt!.EventId);
    }

    [Fact]
    public async Task PartialGroupResultInitializesOnlyItsSuccessfulMember()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var first = StoreFixture.Key("001");
        var second = StoreFixture.Key("002");
        var watch = await fixture.Store.CreateSubscriptionAsync(new NewSubscription(Guid.NewGuid(), "Any section",
            SubscriptionKind.Group, [first, second], true, null, fixture.Clock.GetUtcNow()), default);
        var issued = await fixture.IssueAsync(first, second);
        await fixture.CommitAsync(fixture.Counts(issued, first, 119));
        await fixture.CommitAsync(fixture.Unknown(issued, second));

        var members = (await fixture.Store.FindSubscriptionAsync(watch.Id, default))!.Members;
        Assert.Equal(issued.Token.Ordinal, members.Single(m => m.Key == first).InitializedByAttemptOrdinal);
        Assert.Null(members.Single(m => m.Key == second).InitializedByAttemptOrdinal);
        var failed = await fixture.Store.ReadSectionAsync(second, default);
        Assert.Equal(CheckOutcome.Unavailable, failed.LatestOutcome);
        Assert.Null(failed.Baseline);
        Assert.Null(failed.LastSuccessfulAtUtc);
        Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(20, default));
    }

    [Fact]
    public async Task OverlappingIndividualAndGroupWatchesShareOneDurableEventWithBothRecipients()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        var other = StoreFixture.Key("013");
        var individual = await fixture.AddAsync(key);
        var group = await fixture.Store.CreateSubscriptionAsync(new NewSubscription(Guid.NewGuid(), "Any statistics",
            SubscriptionKind.Group, [key, other], true, null, fixture.Clock.GetUtcNow()), default);

        var result = await fixture.ObserveAsync(key, 119);
        var delivery = Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(20, default));
        Assert.Equal(result.Receipt!.EventId, delivery.EventId);
        Assert.Equal(new[] { individual.Id.ToString("N"), group.Id.ToString("N") }.OrderBy(id => id),
            delivery.Trigger.Recipients.Select(recipient => recipient.SubscriptionId).OrderBy(id => id));
        Assert.All(delivery.Trigger.Recipients, recipient => Assert.Equal(OpeningReason.InitialAvailability, recipient.Reason));
        var storedGroup = (await fixture.Store.FindSubscriptionAsync(group.Id, default))!;
        Assert.NotNull(storedGroup.Members.Single(member => member.Key == key).InitializedByAttemptOrdinal);
        Assert.Null(storedGroup.Members.Single(member => member.Key == other).InitializedByAttemptOrdinal);
    }

    [Fact]
    public async Task PauseAndRemoveInvalidatePreparedRecipientsWithoutDiscardingTheFreshSharedBaseline()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        var first = await fixture.AddAsync(key);
        var second = await fixture.AddAsync(key, label: "Second watch");
        await fixture.ObserveAsync(key, 120);
        var issued = await fixture.IssueAsync(key);
        var completion = fixture.Counts(issued, key, 119);
        var prepared = await fixture.PrepareAsync(completion);
        Assert.NotNull(prepared.Effects.Notification);

        await fixture.Store.ChangeSubscriptionAsync(new PauseSubscription(first.Id, first.DefinitionRevision,
            fixture.Clock.GetUtcNow()), default);
        await fixture.Store.ChangeSubscriptionAsync(new RemoveSubscription(second.Id, second.DefinitionRevision,
            fixture.Clock.GetUtcNow()), default);
        Assert.Equal(CommitWriteStatus.VersionConflict,
            (await fixture.Store.TryCommitSectionAsync(prepared, default)).Status);
        var refreshed = await fixture.CommitAsync(completion);
        Assert.Equal(CommitWriteStatus.Committed, refreshed.Status);
        Assert.Null(refreshed.Receipt!.EventId);
        Assert.Equal(1, (await fixture.Store.ReadSectionAsync(key, default)).Baseline!.Seats.ApparentSeats);
        Assert.Equal(first.Id, Assert.Single(await fixture.Store.ListSubscriptionsAsync(default)).Id);
        Assert.Equal(WatchStatus.Removed, (await fixture.Store.FindSubscriptionAsync(second.Id, default))!.Status);
        Assert.Empty(await fixture.Store.ReadPendingDeliveriesAsync(20, default));
    }

    [Fact]
    public async Task SeatIncreaseSequenceCreatesExactlyTwoStableEventsAndFollowsFallingCounts()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        await fixture.AddAsync(key);
        var eventIds = new List<Guid>();
        foreach (var registered in new[] { 120, 119, 116, 116, 117 })
        {
            var result = await fixture.ObserveAsync(key, registered);
            if (result.Receipt!.EventId is { } id) eventIds.Add(id);
            fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        }

        Assert.Equal(2, eventIds.Count);
        Assert.Equal(2, eventIds.Distinct().Count());
        Assert.Equal(1, (await fixture.Store.ReadDeliveryContextAsync(eventIds[0], default)).Delivery.Trigger.CurrentSeats);
        Assert.Equal(4, (await fixture.Store.ReadDeliveryContextAsync(eventIds[1], default)).Delivery.Trigger.CurrentSeats);
        Assert.Equal(3, (await fixture.Store.ReadSectionAsync(key, default)).Baseline!.Seats.ApparentSeats);
    }

    [Fact]
    public async Task IdenticalReplayAfterRenameAndReopenReturnsTheOriginalEventBeforeVersionChecks()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        var watch = await fixture.AddAsync(key);
        var issued = await fixture.IssueAsync(key);
        var prepared = await fixture.PrepareAsync(fixture.Counts(issued, key, 119));
        var first = await fixture.Store.TryCommitSectionAsync(prepared, default);
        await fixture.Store.ChangeSubscriptionAsync(new RenameSubscription(watch.Id, watch.DefinitionRevision,
            fixture.Clock.GetUtcNow(), "Renamed after commit"), default);
        await fixture.ReopenAsync();

        var replay = await fixture.Store.TryCommitSectionAsync(prepared, default);
        Assert.Equal(CommitWriteStatus.Replayed, replay.Status);
        Assert.Equal(first.Receipt, replay.Receipt);
        var pending = Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(20, default));
        Assert.Equal(first.Receipt!.EventId, pending.EventId);
        Assert.Equal(pending.EventId.ToString("N"), pending.NativeDedupeKey);
        Assert.Equal("Renamed after commit", (await fixture.Store.FindSubscriptionAsync(watch.Id, default))!.Label);
    }

    [Fact]
    public async Task ConflictingReplayIsRejectedWithoutChangingOriginalCountsOrOutbox()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        await fixture.AddAsync(key);
        var issued = await fixture.IssueAsync(key);
        var first = await fixture.CommitAsync(fixture.Counts(issued, key, 119));
        var conflicting = await fixture.PrepareAsync(fixture.Counts(issued, key, 118));

        await Assert.ThrowsAsync<ObservationReplayConflictException>(() => fixture.Store.TryCommitSectionAsync(conflicting, default));
        Assert.Equal(1, (await fixture.Store.ReadSectionAsync(key, default)).Baseline!.Seats.ApparentSeats);
        Assert.Equal(first.Receipt!.EventId, Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(20, default)).EventId);
    }

    [Fact]
    public async Task NewerUnknownCompletionPreventsOlderValidResultFromRegressingHealthOrBaseline()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        await fixture.AddAsync(key, initialNotice: false);
        await fixture.ObserveAsync(key, 120);
        var baselineSuccessAt = (await fixture.Store.ReadSectionAsync(key, default)).LastSuccessfulAtUtc;
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var older = await fixture.IssueAsync(key);
        var newer = await fixture.IssueAsync(key);
        await fixture.CommitAsync(fixture.Unknown(newer, key));
        var before = await fixture.Store.ReadSectionAsync(key, default);

        var stale = await fixture.CommitAsync(fixture.Counts(older, key, 119));
        Assert.Equal(CommitDisposition.IgnoredStale, stale.Receipt!.Disposition);
        Assert.Equal(before, await fixture.Store.ReadSectionAsync(key, default));
        Assert.Equal(newer.Token.Ordinal, before.LatestCompletedOrdinal);
        Assert.Equal(CheckOutcome.Unavailable, before.LatestOutcome);
        Assert.Equal(baselineSuccessAt, before.LastSuccessfulAtUtc);
        Assert.Equal(fixture.Clock.GetUtcNow(), before.LatestAttemptAtUtc);
        Assert.Equal(0, before.Baseline!.Seats.ApparentSeats);
        Assert.Empty(await fixture.Store.ReadPendingDeliveriesAsync(20, default));
    }

    [Fact]
    public async Task ConcurrentPreparedVersionsHaveOneWinnerAndRetryCannotRollbackNewerState()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        await fixture.AddAsync(key, initialNotice: false);
        var older = await fixture.IssueAsync(key);
        var newer = await fixture.IssueAsync(key);
        var oldCompletion = fixture.Counts(older, key, 120);
        var newCompletion = fixture.Counts(newer, key, 119);
        var first = await fixture.PrepareAsync(oldCompletion);
        var second = await fixture.PrepareAsync(newCompletion);
        await using var competitor = await SqliteWatchStore.OpenAsync(new SqliteConnectionFactory(fixture.DatabasePath), fixture.Clock);

        var results = await Task.WhenAll(fixture.Store.TryCommitSectionAsync(first, default),
            competitor.TryCommitSectionAsync(second, default));
        Assert.Single(results, result => result.Status == CommitWriteStatus.Committed);
        Assert.Single(results, result => result.Status == CommitWriteStatus.VersionConflict);
        var retryCompletion = results[0].Status == CommitWriteStatus.VersionConflict ? oldCompletion : newCompletion;
        var retry = await fixture.CommitAsync(retryCompletion);
        Assert.Equal(CommitWriteStatus.Committed, retry.Status);
        var current = await fixture.Store.ReadSectionAsync(key, default);
        Assert.Equal(newer.Token.Ordinal, current.LatestCompletedOrdinal);
        Assert.Equal(1, current.Baseline!.Seats.ApparentSeats);
    }

    [Fact]
    public async Task QuotedUnicodeLabelAndMutableCallerListCannotAlterSchemaOrOwnedMembership()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        var callerList = new List<SectionKey> { key };
        const string label = "서울 '); DROP TABLE StoreMetadata; --";
        var command = new NewSubscription(Guid.NewGuid(), label, SubscriptionKind.Section, callerList, true, null, fixture.Clock.GetUtcNow());
        callerList.Clear();
        var created = await fixture.Store.CreateSubscriptionAsync(command, default);
        await fixture.ReopenAsync();

        var stored = (await fixture.Store.FindSubscriptionAsync(created.Id, default))!;
        Assert.Equal(label, stored.Label);
        Assert.Equal(key, Assert.Single(stored.Members).Key);
        Assert.Equal(1, (await fixture.IssueAsync(key)).Token.Ordinal);
    }

    [Fact]
    public async Task PendingDeliverySurvivesReopenAndDeliveryWritesEnforceRevisionAndTerminalState()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        await fixture.AddAsync(key);
        var result = await fixture.ObserveAsync(key, 119);
        await fixture.ReopenAsync();
        var delivery = Assert.Single(await fixture.Store.ReadPendingDeliveriesAsync(20, default));
        Assert.Equal(result.Receipt!.EventId, delivery.EventId);

        var retry = new DeliveryResultCommand(delivery.EventId, delivery.DeliveryRevision, DeliveryStatus.Pending, 1, fixture.Clock.GetUtcNow());
        Assert.Equal(DeliveryWriteResult.Applied, await fixture.Store.RecordDeliveryResultAsync(retry, default));
        Assert.Equal(DeliveryWriteResult.VersionConflict, await fixture.Store.RecordDeliveryResultAsync(
            new DeliveryResultCommand(delivery.EventId, delivery.DeliveryRevision, DeliveryStatus.OsAccepted, 2, fixture.Clock.GetUtcNow()), default));
        delivery = (await fixture.Store.ReadDeliveryContextAsync(delivery.EventId, default)).Delivery;
        Assert.Equal(DeliveryWriteResult.VersionConflict, await fixture.Store.RecordDeliveryResultAsync(
            new DeliveryResultCommand(delivery.EventId, delivery.DeliveryRevision, DeliveryStatus.Pending, 0, fixture.Clock.GetUtcNow()), default));
        Assert.Equal(DeliveryWriteResult.Applied, await fixture.Store.RecordDeliveryResultAsync(
            new DeliveryResultCommand(delivery.EventId, delivery.DeliveryRevision, DeliveryStatus.OsAccepted, 2, fixture.Clock.GetUtcNow()), default));

        var terminal = (await fixture.Store.ReadDeliveryContextAsync(delivery.EventId, default)).Delivery;
        Assert.Equal(DeliveryWriteResult.VersionConflict, await fixture.Store.RecordDeliveryResultAsync(
            new DeliveryResultCommand(terminal.EventId, terminal.DeliveryRevision, DeliveryStatus.Pending, 3, fixture.Clock.GetUtcNow()), default));
        Assert.Empty(await fixture.Store.ReadPendingDeliveriesAsync(20, default));
        Assert.Equal(DeliveryStatus.OsAccepted, (await fixture.Store.ReadDeliveryContextAsync(delivery.EventId, default)).Delivery.Status);
    }

    [Fact]
    public async Task ExpiryBetweenPreparationAndTransactionRequiresFreshEligibilityWithoutWritingOldEffects()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        fixture.Clock.Set(new DateTimeOffset(2026, 11, 1, 14, 59, 59, TimeSpan.Zero));
        var key = StoreFixture.Key();
        var expiry = WatchExpiry.FromLocalDate(new DateOnly(2026, 11, 1));
        var watch = await fixture.Store.CreateSubscriptionAsync(fixture.NewSection(key, expiry: expiry), default);
        var issued = await fixture.IssueAsync(key);
        var completion = fixture.Counts(issued, key, 119);
        var prepared = await fixture.PrepareAsync(completion);
        Assert.NotNull(prepared.Effects.Notification);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));

        var obsolete = await fixture.Store.TryCommitSectionAsync(prepared, default);
        Assert.Equal(CommitWriteStatus.VersionConflict, obsolete.Status);
        Assert.Equal(0, (await fixture.Store.ReadSectionAsync(key, default)).LatestCompletedOrdinal);
        var refreshed = await fixture.CommitAsync(completion);
        Assert.Equal(CommitWriteStatus.Committed, refreshed.Status);
        Assert.Null(refreshed.Receipt!.EventId);
        Assert.Null(Assert.Single((await fixture.Store.FindSubscriptionAsync(watch.Id, default))!.Members).InitializedByAttemptOrdinal);
        Assert.Empty(await fixture.Store.ReadPendingDeliveriesAsync(20, default));
    }

    [Fact]
    public async Task ExtendingAnElapsedActiveExpiryStillRequiresExplicitResumeToStartANewGeneration()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        fixture.Clock.Set(new DateTimeOffset(2026, 11, 1, 14, 59, 0, TimeSpan.Zero));
        var key = StoreFixture.Key();
        var expiry = WatchExpiry.FromLocalDate(new DateOnly(2026, 11, 1));
        var watch = await fixture.Store.CreateSubscriptionAsync(fixture.NewSection(key, expiry: expiry), default);
        var initialized = await fixture.ObserveAsync(key, 120);
        watch = (await fixture.Store.FindSubscriptionAsync(watch.Id, default))!;
        var originalInitialization = Assert.Single(watch.Members).InitializedByAttemptOrdinal;
        Assert.NotNull(originalInitialization);
        Assert.Null(initialized.Receipt!.EventId);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        var inflight = await fixture.IssueAsync(key);

        var extended = await fixture.Store.ChangeSubscriptionAsync(new SetSubscriptionExpiry(watch.Id, watch.DefinitionRevision,
            fixture.Clock.GetUtcNow(), WatchExpiry.FromLocalDate(new DateOnly(2026, 11, 3))), default);
        Assert.Equal(WatchStatus.Expired, extended.Status);
        Assert.Equal(1, extended.ActivationGeneration);
        Assert.Equal(originalInitialization, Assert.Single(extended.Members).InitializedByAttemptOrdinal);

        var resumed = await fixture.Store.ChangeSubscriptionAsync(new ResumeSubscription(extended.Id, extended.DefinitionRevision,
            fixture.Clock.GetUtcNow()), default);
        Assert.Equal(WatchStatus.Active, resumed.Status);
        Assert.Equal(2, resumed.ActivationGeneration);
        Assert.Null(Assert.Single(resumed.Members).InitializedByAttemptOrdinal);
        Assert.Equal(inflight.Token.Ordinal, Assert.Single(resumed.Members).ActivationWatermark);
    }
}
