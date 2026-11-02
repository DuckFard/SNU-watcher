using SnuWatcher.Application.Observations;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Observations;

public sealed class ObservationEligibilityTests
{
    [Fact]
    public void OverlappingWatchesShareOneNotificationWithGuidRecipients()
    {
        var completion = ObservationFixture.Valid(2);
        var watches = new[]
        {
            ObservationFixture.Watch(initialized: true),
            ObservationFixture.Watch(ObservationFixture.SecondId, initialized: true),
        };
        var commit = ObservationPreparer.Prepare(ObservationFixture.Context(completion,
            ObservationFixture.State(), watches), completion, completion.ObservedAtUtc);

        Assert.Equal(1, commit.Effects.Notification!.CurrentSeats);
        Assert.Equal(new[] { ObservationFixture.FirstId, ObservationFixture.SecondId },
            commit.Effects.Notification.Recipients.Select(r => Guid.ParseExact(r.SubscriptionId, "N")));
        Assert.Empty(commit.Effects.Initializations);
    }

    [Fact]
    public void NewSilentWatchInitializesWhileOnlyAnOlderWatchReceivesTheSharedIncrease()
    {
        var completion = ObservationFixture.Valid(2);
        var watches = new[]
        {
            ObservationFixture.Watch(initialized: true),
            ObservationFixture.Watch(ObservationFixture.SecondId, initial: false, watermark: 1),
        };
        var commit = ObservationPreparer.Prepare(ObservationFixture.Context(completion,
            ObservationFixture.State(), watches), completion, completion.ObservedAtUtc);

        Assert.Equal(ObservationFixture.FirstId.ToString("N"), Assert.Single(commit.Effects.Notification!.Recipients).SubscriptionId);
        Assert.Equal(new MemberInitializationEffect(ObservationFixture.SecondId, 1), Assert.Single(commit.Effects.Initializations));
    }

    [Fact]
    public void NewWatchCanReceiveInitialAvailabilityWithoutASectionLevelIncrease()
    {
        var completion = ObservationFixture.Valid(2);
        var commit = ObservationPreparer.Prepare(ObservationFixture.Context(completion,
            ObservationFixture.State(registered: 119), [ObservationFixture.Watch(watermark: 1)]),
            completion, completion.ObservedAtUtc);

        Assert.Equal(EvaluationOutcome.NoOpening, commit.Effects.EvaluationOutcome);
        Assert.Equal(OpeningReason.InitialAvailability, Assert.Single(commit.Effects.Notification!.Recipients).Reason);
        Assert.Single(commit.Effects.Initializations);
    }

    [Theory]
    [InlineData(WatchStatus.Paused)]
    [InlineData(WatchStatus.Expired)]
    [InlineData(WatchStatus.Removed)]
    public void InactiveWatchesNeitherInitializeNorReceiveAlerts(WatchStatus status)
    {
        var completion = ObservationFixture.Valid(1);
        var commit = ObservationPreparer.Prepare(ObservationFixture.Context(completion,
            watches: [ObservationFixture.Watch(status: status)]), completion, completion.ObservedAtUtc);

        Assert.NotNull(commit.Effects.NextBaseline);
        Assert.Empty(commit.Effects.Initializations);
        Assert.Null(commit.Effects.Notification);
    }

    [Fact]
    public void ExpiryBetweenObservationAndEvaluationExcludesOnlyTheExpiredRecipient()
    {
        var expiry = WatchExpiry.FromLocalDate(new DateOnly(2026, 10, 31), TimeZoneInfo.Utc);
        var completion = ObservationFixture.Valid(2, observedAt: expiry.ExclusiveUtc.AddMinutes(-1));
        var watches = new[]
        {
            ObservationFixture.Watch(expiry: expiry),
            ObservationFixture.Watch(ObservationFixture.SecondId),
        };
        var commit = ObservationPreparer.Prepare(ObservationFixture.Context(completion, watches: watches),
            completion, expiry.ExclusiveUtc);

        Assert.Equal(ObservationFixture.SecondId.ToString("N"), Assert.Single(commit.Effects.Notification!.Recipients).SubscriptionId);
        Assert.Equal(ObservationFixture.SecondId, Assert.Single(commit.Effects.Initializations).SubscriptionId);
        Assert.Equal(completion.ObservedAtUtc, commit.Effects.Notification.ObservedAt);
        Assert.Equal(expiry.ExclusiveUtc, commit.EvaluatedAtUtc);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void AttemptIssuedBeforeOrAtResumeCannotInitializeThatGeneration(long ordinal)
    {
        var completion = ObservationFixture.Valid(ordinal);
        var watches = new[]
        {
            ObservationFixture.Watch(initialized: true),
            ObservationFixture.Watch(ObservationFixture.SecondId, generation: 2, watermark: 5),
        };
        var commit = ObservationPreparer.Prepare(ObservationFixture.Context(completion,
            ObservationFixture.State(), watches), completion, completion.ObservedAtUtc);

        Assert.Equal(ObservationFixture.FirstId.ToString("N"), Assert.Single(commit.Effects.Notification!.Recipients).SubscriptionId);
        Assert.Empty(commit.Effects.Initializations);
    }

    [Fact]
    public void FirstAttemptAfterResumeInitializesItsNewGeneration()
    {
        var completion = ObservationFixture.Valid(6);
        var commit = ObservationPreparer.Prepare(ObservationFixture.Context(completion,
            ObservationFixture.State(registered: 119),
            [ObservationFixture.Watch(ObservationFixture.SecondId, generation: 2, watermark: 5)]),
            completion, completion.ObservedAtUtc);

        Assert.Equal(new MemberInitializationEffect(ObservationFixture.SecondId, 2), Assert.Single(commit.Effects.Initializations));
        Assert.Equal(2, Assert.Single(commit.Effects.Notification!.Recipients).Generation);
        Assert.Equal(OpeningReason.InitialAvailability, Assert.Single(commit.Effects.Notification.Recipients).Reason);
    }

    [Fact]
    public void PartialGroupObservationInitializesOnlyTheSuccessfulSection()
    {
        var first = ObservationFixture.Key();
        var second = ObservationFixture.Key("013");
        var watch = ObservationFixture.Watch(members: [new(first, 0, null), new(second, 0, null)]);
        var completion = ObservationFixture.Valid(1);
        var commit = ObservationPreparer.Prepare(ObservationFixture.Context(completion, watches: [watch]),
            completion, completion.ObservedAtUtc);
        var (_, watches) = ObservationFixture.Apply(SectionSnapshot.Unchecked(first), [watch], commit);
        var unavailable = ObservationFixture.Unknown(2, CheckOutcome.Incomplete, second);
        var partial = ObservationPreparer.Prepare(ObservationFixture.Context(unavailable, watches: watches),
            unavailable, unavailable.ObservedAtUtc);

        Assert.Equal(1, watches[0].Members.Single(m => m.Key == first).InitializedByAttemptOrdinal);
        Assert.Null(watches[0].Members.Single(m => m.Key == second).InitializedByAttemptOrdinal);
        Assert.Empty(partial.Effects.Initializations);
        Assert.Null(partial.Effects.Notification);
    }

    [Fact]
    public void ContextMustIdentifyTheExactIssuedTokenAndSection()
    {
        var completion = ObservationFixture.Valid(1);
        var correct = ObservationFixture.Context(completion);
        var otherToken = new IssuedCheck(new CheckAttemptToken(Guid.NewGuid(), 1), ObservationFixture.Now, [completion.Key]);
        var wrongToken = new CommitContext(otherToken, correct.Section, 7, []);
        var otherKey = ObservationFixture.Key("013");
        var wrongKey = new CommitContext(new IssuedCheck(completion.Token, ObservationFixture.Now, [otherKey]),
            SectionSnapshot.Unchecked(otherKey), 7, []);

        Assert.Throws<ArgumentException>(() => ObservationPreparer.Prepare(wrongToken, completion, ObservationFixture.Now));
        Assert.Throws<ArgumentException>(() => ObservationPreparer.Prepare(wrongKey, completion, ObservationFixture.Now));
        Assert.Throws<ArgumentNullException>(() => ObservationPreparer.Prepare(null!, completion, ObservationFixture.Now));
        Assert.Throws<ArgumentNullException>(() => ObservationPreparer.Prepare(correct, null!, ObservationFixture.Now));
    }
}
