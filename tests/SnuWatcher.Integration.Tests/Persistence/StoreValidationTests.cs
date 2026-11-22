using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreValidationTests
{
    [Fact]
    public async Task OriginalCreateRetryAfterRenameReturnsCurrentStateWithoutResettingIt()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var original = fixture.NewSection(StoreFixture.Key());
        var watch = await fixture.Store.CreateSubscriptionAsync(original, default);
        await fixture.ObserveAsync(StoreFixture.Key(), 119);
        watch = (await fixture.Store.FindSubscriptionAsync(watch.Id, default))!;
        var renamed = await fixture.Store.ChangeSubscriptionAsync(new RenameSubscription(watch.Id, watch.DefinitionRevision,
            fixture.Clock.GetUtcNow(), "New name"), default);

        var replay = await fixture.Store.CreateSubscriptionAsync(original, default);

        Assert.Equal("New name", replay.Label);
        Assert.Equal(renamed.DefinitionRevision, replay.DefinitionRevision);
        Assert.Equal(renamed.ActivationGeneration, replay.ActivationGeneration);
        Assert.Equal(Assert.Single(renamed.Members).InitializedByAttemptOrdinal, Assert.Single(replay.Members).InitializedByAttemptOrdinal);
        Assert.Single(await fixture.Store.ListSubscriptionsAsync(default));
    }

    [Fact]
    public async Task ForgedTokensAndSectionsOutsideScopeAreRejected()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        var issued = await fixture.IssueAsync(key);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.ReadCommitContextAsync(
            new CheckAttemptToken(issued.Token.Id, issued.Token.Ordinal + 1), key, default));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.ReadCommitContextAsync(
            new CheckAttemptToken(Guid.NewGuid(), issued.Token.Ordinal), key, default));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.ReadCommitContextAsync(issued.Token,
            StoreFixture.Key(section: "001"), default));
        Assert.Equal(0, (await fixture.Store.ReadSectionAsync(key, default)).Version);
    }

    [Fact]
    public async Task AlteredPreparedRecipientsCannotInsertAnUnrelatedAlert()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        await fixture.AddAsync(key);
        var issued = await fixture.IssueAsync(key);
        var prepared = await fixture.PrepareAsync(fixture.Counts(issued, key, 119));
        var forgedNotification = prepared.Effects.Notification! with
        { Recipients = [new Recipient(Guid.NewGuid().ToString("N"), 1, OpeningReason.InitialAvailability)] };
        var forged = new PreparedSectionCommit(prepared.Completion, prepared.ExpectedSectionVersion, prepared.ExpectedCatalogRevision,
            new SectionEffects(prepared.Effects.EvaluationOutcome, prepared.Effects.HealthOutcome, prepared.Effects.NextBaseline,
                prepared.Effects.Initializations, forgedNotification), prepared.EvaluatedAtUtc);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.TryCommitSectionAsync(forged, default));

        Assert.Equal(0, (await fixture.Store.ReadSectionAsync(key, default)).Version);
        Assert.Empty(await fixture.Store.ReadPendingDeliveriesAsync(20, default));
        Assert.NotNull((await fixture.Store.TryCommitSectionAsync(prepared, default)).Receipt!.EventId);
    }

    [Fact]
    public async Task FullSixPartIdentityKeepsDifferentTermsAndSubsubjectsSeparate()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var first = StoreFixture.Key();
        var term = new SectionKey(first.Year, first.SemesterCode, "U000300003", first.CourseCode, first.Section, first.SubsubjectCode);
        var subsubject = new SectionKey(first.Year, first.SemesterCode, first.DetailSemesterCode, first.CourseCode, first.Section, "1");
        await fixture.AddAsync(first);
        await fixture.AddAsync(term);
        await fixture.AddAsync(subsubject);

        await fixture.ObserveAsync(first, 120);
        await fixture.ObserveAsync(term, 119);
        await fixture.ObserveAsync(subsubject, 118);
        await fixture.ReopenAsync();

        Assert.Equal(0, (await fixture.Store.ReadSectionAsync(first, default)).Baseline!.Seats.ApparentSeats);
        Assert.Equal(1, (await fixture.Store.ReadSectionAsync(term, default)).Baseline!.Seats.ApparentSeats);
        Assert.Equal(2, (await fixture.Store.ReadSectionAsync(subsubject, default)).Baseline!.Seats.ApparentSeats);
        Assert.Equal(3, (await fixture.Store.ListSubscriptionsAsync(default)).Length);
    }

    [Fact]
    public async Task SemanticsChangeAdvancesHealthButPreservesTheLastValidBaselineAndSuccess()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var key = StoreFixture.Key();
        await fixture.AddAsync(key, initialNotice: false);
        await fixture.ObserveAsync(key, 120);
        var before = await fixture.Store.ReadSectionAsync(key, default);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var issued = await fixture.IssueAsync(key);
        var changed = new SectionCompletion(issued.Token, SeatObservation.Valid(key, issued.Token.Ordinal,
            fixture.Clock.GetUtcNow(), new SeatCount(110, 120), new QuotaMeaning("different-verified-meaning")), CheckOutcome.Success);

        var result = await fixture.CommitAsync(changed);
        await fixture.ReopenAsync();
        var after = await fixture.Store.ReadSectionAsync(key, default);

        Assert.Equal(CommitDisposition.RevalidationRequired, result.Receipt!.Disposition);
        Assert.Equal(CheckOutcome.ParseChanged, after.LatestOutcome);
        Assert.Equal(issued.Token.Ordinal, after.LatestCompletedOrdinal);
        Assert.Equal(before.Baseline, after.Baseline);
        Assert.Equal(before.LastSuccessfulAtUtc, after.LastSuccessfulAtUtc);
        Assert.Empty(await fixture.Store.ReadPendingDeliveriesAsync(20, default));
    }
}
