using System.Collections.Immutable;
using SnuWatcher.Application.Observations;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Observations;

public sealed class ObservationPreparerTests
{
    [Fact]
    public void EveryIncreaseCreatesAnOpeningButUnchangedOrFallingCountsOnlyAdvanceBaseline()
    {
        var section = SectionSnapshot.Unchecked(ObservationFixture.Key());
        ImmutableArray<SubscriptionSnapshot> watches = [ObservationFixture.Watch(initial: false)];
        ImmutableArray<OpeningNotification> notices = [];
        var registered = new[] { 120, 119, 116, 116, 117, 118 };
        for (var i = 0; i < registered.Length; i++)
        {
            var completion = ObservationFixture.Valid(i + 1, registered[i]);
            var commit = ObservationPreparer.Prepare(ObservationFixture.Context(completion, section, watches),
                completion, completion.ObservedAtUtc);
            if (commit.Effects.Notification is { } notification) notices = notices.Add(notification);
            (section, watches) = ObservationFixture.Apply(section, watches, commit);
        }

        Assert.Equal(new[] { 1, 4 }, notices.Select(n => n.CurrentSeats));
        Assert.Equal(new int?[] { 0, 1 }, notices.Select(n => n.PreviousSeats));
        Assert.Equal(6, section.LatestCompletedOrdinal);
        Assert.Equal(2, section.Baseline!.Seats.ApparentSeats);
        Assert.All(notices, n => Assert.Equal(OpeningReason.IncreasedSeats, Assert.Single(n.Recipients).Reason));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FirstOpenObservationInitializesAndHonorsTheInitialNoticeChoice(bool initial)
    {
        var completion = ObservationFixture.Valid(1);
        var commit = ObservationPreparer.Prepare(ObservationFixture.Context(completion,
            watches: [ObservationFixture.Watch(initial: initial)]), completion, completion.ObservedAtUtc);

        Assert.Equal(EvaluationOutcome.Opening, commit.Effects.EvaluationOutcome);
        Assert.Equal(1, commit.Effects.NextBaseline!.Seats.ApparentSeats);
        Assert.Equal(ObservationFixture.FirstId, Assert.Single(commit.Effects.Initializations).SubscriptionId);
        if (initial)
            Assert.Equal(OpeningReason.InitialAvailability, Assert.Single(commit.Effects.Notification!.Recipients).Reason);
        else
            Assert.Null(commit.Effects.Notification);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(120, 120)]
    [InlineData(121, 120)]
    public void ZeroFullAndOverCapacityCountsRemainValidWithoutAnOpening(int registered, int capacity)
    {
        var completion = ObservationFixture.Valid(1, registered, capacity);
        var commit = ObservationPreparer.Prepare(ObservationFixture.Context(completion,
            watches: [ObservationFixture.Watch()]), completion, completion.ObservedAtUtc);

        Assert.Equal(EvaluationOutcome.BaselineEstablished, commit.Effects.EvaluationOutcome);
        Assert.Equal(CheckOutcome.Success, commit.Effects.HealthOutcome);
        Assert.Equal(0, commit.Effects.NextBaseline!.Seats.ApparentSeats);
        Assert.Single(commit.Effects.Initializations);
        Assert.Null(commit.Effects.Notification);
    }

    [Fact]
    public void AnIncreasedQuotaIsAnOpeningWithTheVerifiedSameMeaning()
    {
        var completion = ObservationFixture.Valid(2, 120, 121);
        var commit = ObservationPreparer.Prepare(ObservationFixture.Context(completion, ObservationFixture.State(),
            [ObservationFixture.Watch(initialized: true)]), completion, completion.ObservedAtUtc);

        Assert.Equal(1, commit.Effects.Notification!.CurrentSeats);
        Assert.Equal(0, commit.Effects.Notification.PreviousSeats);
        Assert.Equal(OpeningReason.IncreasedSeats, Assert.Single(commit.Effects.Notification.Recipients).Reason);
    }

    [Theory]
    [InlineData(CheckOutcome.Unavailable)]
    [InlineData(CheckOutcome.RateLimited)]
    [InlineData(CheckOutcome.AccessRestricted)]
    [InlineData(CheckOutcome.ParseChanged)]
    [InlineData(CheckOutcome.NotFound)]
    [InlineData(CheckOutcome.Incomplete)]
    public void UnknownCompletionChangesHealthWithoutReplacingTheValidBaselineOrInitializingMembers(CheckOutcome outcome)
    {
        var state = ObservationFixture.State();
        var completion = ObservationFixture.Unknown(2, outcome);
        var commit = ObservationPreparer.Prepare(ObservationFixture.Context(completion, state,
            [ObservationFixture.Watch()]), completion, completion.ObservedAtUtc);

        Assert.Equal(EvaluationOutcome.IgnoredUnknown, commit.Effects.EvaluationOutcome);
        Assert.Equal(outcome, commit.Effects.HealthOutcome);
        Assert.Null(commit.Effects.NextBaseline);
        Assert.Empty(commit.Effects.Initializations);
        Assert.Null(commit.Effects.Notification);
        Assert.Equal(1, state.Baseline!.AttemptSequence);
        Assert.Equal(state.Baseline.ObservedAt, state.LastSuccessfulAtUtc);
    }

    [Fact]
    public void UnverifiedQuotaLabelRequiresRevalidation()
    {
        var token = new CheckAttemptToken(Guid.NewGuid(), 2);
        var observation = SeatObservation.FromSource(ObservationFixture.Key(), 2, ObservationFixture.Now,
            110, 120, "unverified quota");
        var completion = new SectionCompletion(token, observation, CheckOutcome.ParseChanged);
        var commit = ObservationPreparer.Prepare(ObservationFixture.Context(completion, ObservationFixture.State(),
            [ObservationFixture.Watch()]), completion, ObservationFixture.Now);

        Assert.Equal(EvaluationOutcome.RevalidationRequired, commit.Effects.EvaluationOutcome);
        Assert.Equal(CheckOutcome.ParseChanged, commit.Effects.HealthOutcome);
        Assert.Null(commit.Effects.NextBaseline);
        Assert.Empty(commit.Effects.Initializations);
        Assert.Null(commit.Effects.Notification);
    }

    [Fact]
    public void ChangedQuotaMeaningRequiresRevalidationEvenWhenRawCountsAreValid()
    {
        var completion = ObservationFixture.Valid(2, 110, meaning: new QuotaMeaning("different-verified-meaning"));
        var commit = ObservationPreparer.Prepare(ObservationFixture.Context(completion, ObservationFixture.State(),
            [ObservationFixture.Watch()]), completion, completion.ObservedAtUtc);

        Assert.Equal(EvaluationOutcome.RevalidationRequired, commit.Effects.EvaluationOutcome);
        Assert.Equal(CheckOutcome.ParseChanged, commit.Effects.HealthOutcome);
        Assert.Null(commit.Effects.NextBaseline);
        Assert.Empty(commit.Effects.Initializations);
        Assert.Null(commit.Effects.Notification);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ANewerUnknownCompletionBlocksAnOlderOrRepeatedValidObservation(long incomingOrdinal)
    {
        var baseline = ObservationFixture.State().Baseline!;
        var current = new SectionSnapshot(baseline.Key, 2, 3, CheckOutcome.Unavailable,
            ObservationFixture.Now.AddMinutes(3), baseline.ObservedAt, baseline);
        var completion = ObservationFixture.Valid(incomingOrdinal, 100);
        var commit = ObservationPreparer.Prepare(ObservationFixture.Context(completion, current,
            [ObservationFixture.Watch()]), completion, ObservationFixture.Now.AddMinutes(4));

        Assert.Equal(EvaluationOutcome.IgnoredStale, commit.Effects.EvaluationOutcome);
        Assert.Equal(CheckOutcome.Unavailable, commit.Effects.HealthOutcome);
        Assert.Null(commit.Effects.NextBaseline);
        Assert.Empty(commit.Effects.Initializations);
        Assert.Null(commit.Effects.Notification);
        Assert.Equal(2, commit.ExpectedSectionVersion);
    }

    [Fact]
    public void StaleCompletionPreservesSuccessfulHealthWithoutReemittingAnOpening()
    {
        var completion = ObservationFixture.Valid(1, 100);
        var commit = ObservationPreparer.Prepare(ObservationFixture.Context(completion, ObservationFixture.State(),
            [ObservationFixture.Watch(initialized: true)]), completion, completion.ObservedAtUtc);

        Assert.Equal(EvaluationOutcome.IgnoredStale, commit.Effects.EvaluationOutcome);
        Assert.Equal(CheckOutcome.Success, commit.Effects.HealthOutcome);
        Assert.Null(commit.Effects.Notification);
    }

    [Fact]
    public void AttemptOrderAcceptsANewerResponseDespiteAReversedWallClock()
    {
        var completion = ObservationFixture.Valid(2, observedAt: ObservationFixture.Now.AddDays(-1));
        var commit = ObservationPreparer.Prepare(ObservationFixture.Context(completion, ObservationFixture.State(),
            [ObservationFixture.Watch(initialized: true)]), completion, ObservationFixture.Now.AddDays(-1));

        Assert.Equal(EvaluationOutcome.Opening, commit.Effects.EvaluationOutcome);
        Assert.Equal(completion.ObservedAtUtc, commit.Effects.NextBaseline!.ObservedAt);
        Assert.Equal(2, commit.Effects.Notification!.AttemptSequence);
        Assert.Equal(1, commit.Effects.Notification.CurrentSeats);
    }

    [Fact]
    public void PreparedCommitCarriesImmutableSourceFactsAndExpectedVersions()
    {
        var completion = ObservationFixture.Valid(2);
        var evaluationTime = ObservationFixture.Now.ToOffset(TimeSpan.FromHours(9));
        var commit = ObservationPreparer.Prepare(ObservationFixture.Context(completion, ObservationFixture.State(),
            catalogRevision: 19), completion, evaluationTime);

        Assert.Same(completion, commit.Completion);
        Assert.Equal(1, commit.ExpectedSectionVersion);
        Assert.Equal(19, commit.ExpectedCatalogRevision);
        Assert.Equal(evaluationTime.ToUniversalTime(), commit.EvaluatedAtUtc);
        Assert.Equal(TimeSpan.Zero, commit.EvaluatedAtUtc.Offset);
    }
}
