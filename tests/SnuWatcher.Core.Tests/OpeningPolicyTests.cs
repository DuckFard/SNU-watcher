using Xunit;

namespace SnuWatcher.Core.Tests;

public sealed class OpeningPolicyTests
{
    private static readonly SectionKey Key = Fixture.Key();

    [Fact]
    public void FullSectionThenEachIncreaseAlertsExactlyTwiceThenAgainAfterDip()
    {
        // 120/120 (baseline), 119, 116, 116, 117, 116 (the user's real V10.128 sequence).
        var r = Fixture.Run(Key, (120, 120), (119, 120), (116, 120), (116, 120), (117, 120), (116, 120));

        Assert.Equal(EvaluationOutcome.BaselineEstablished, r[0].Outcome);
        Assert.Equal(EvaluationOutcome.Opening, r[1].Outcome);
        Assert.Equal(1, r[1].Opening!.CurrentSeats);
        Assert.Equal(OpeningReason.IncreasedSeats, r[1].Opening!.Reason);
        Assert.Equal(EvaluationOutcome.Opening, r[2].Outcome);
        Assert.Equal(4, r[2].Opening!.CurrentSeats);
        Assert.Equal(3, r[2].Opening!.Increase);
        Assert.Equal(EvaluationOutcome.NoOpening, r[3].Outcome);   // unchanged
        Assert.Equal(EvaluationOutcome.NoOpening, r[4].Outcome);   // fewer seats, baseline follows
        Assert.Equal(EvaluationOutcome.Opening, r[5].Outcome);     // 3 -> 4 alerts again
        Assert.Equal(3, r[5].PreviousSeats);
    }

    [Fact]
    public void FirstSightOfAnOpenSectionIsInitialAvailability()
    {
        var e = Fixture.Run(Key, (119, 120))[0];
        Assert.Equal(EvaluationOutcome.Opening, e.Outcome);
        Assert.Equal(OpeningReason.InitialAvailability, e.Opening!.Reason);
        Assert.Null(e.Opening.PreviousSeats);
        Assert.Equal(1, e.Opening.Increase);
    }

    [Fact]
    public void FirstSightOfAFullSectionOnlyEstablishesABaseline()
    {
        var e = Fixture.Run(Key, (120, 120))[0];
        Assert.Equal(EvaluationOutcome.BaselineEstablished, e.Outcome);
        Assert.Null(e.Opening);
        Assert.NotNull(e.NextBaseline);
    }

    [Fact]
    public void QuotaIncreaseWithSameEnrollmentAlerts()
    {
        var r = Fixture.Run(Key, (20, 20), (20, 25));
        Assert.Equal(EvaluationOutcome.Opening, r[1].Outcome);
        Assert.Equal(5, r[1].Opening!.CurrentSeats);
    }

    [Fact]
    public void LargeValidQuotaIncreaseIsNotSuppressedByMagnitude()
    {
        var r = Fixture.Run(Key, (20, 20), (20, 200));
        Assert.Equal(EvaluationOutcome.Opening, r[1].Outcome);
        Assert.Equal(180, r[1].Opening!.Increase);
    }

    [Theory]
    [InlineData(130, 120)]
    [InlineData(0, 0)]
    public void OverCapacityAndZeroCapacityNeverAlert(int registered, int capacity)
    {
        var r = Fixture.Run(Key, (registered, capacity), (registered, capacity));
        Assert.Equal(EvaluationOutcome.BaselineEstablished, r[0].Outcome);
        Assert.Equal(EvaluationOutcome.NoOpening, r[1].Outcome);
    }

    [Fact]
    public void UnknownResponsesPreserveTheBaselineAcrossAGap()
    {
        var first = OpeningPolicy.Evaluate(null, Fixture.Valid(Key, 1, 120, 120));
        var unknown = OpeningPolicy.Evaluate(first.NextBaseline, SeatObservation.Unknown(Key, 2, Fixture.At(2)));
        Assert.Equal(EvaluationOutcome.IgnoredUnknown, unknown.Outcome);
        Assert.Same(first.NextBaseline, unknown.NextBaseline);

        var after = OpeningPolicy.Evaluate(unknown.NextBaseline, Fixture.Valid(Key, 3, 119, 120));
        Assert.Equal(EvaluationOutcome.Opening, after.Outcome);
        Assert.Equal(0, after.PreviousSeats);
    }

    [Fact]
    public void ReplayedOrOutOfOrderAttemptsAreIgnored()
    {
        var first = OpeningPolicy.Evaluate(null, Fixture.Valid(Key, 5, 120, 120));
        var replay = OpeningPolicy.Evaluate(first.NextBaseline, Fixture.Valid(Key, 5, 100, 120));
        var older = OpeningPolicy.Evaluate(first.NextBaseline, Fixture.Valid(Key, 4, 100, 120));
        Assert.Equal(EvaluationOutcome.IgnoredStale, replay.Outcome);
        Assert.Equal(EvaluationOutcome.IgnoredStale, older.Outcome);
        Assert.Same(first.NextBaseline, replay.NextBaseline);
        Assert.False(replay.Accepted);
    }

    [Fact]
    public void ChangedQuotaMeaningRequiresRevalidationAndKeepsTheBaseline()
    {
        var first = OpeningPolicy.Evaluate(null, Fixture.Valid(Key, 1, 120, 120));
        var other = SeatObservation.Valid(Key, 2, Fixture.At(2), new SeatCount(1, 120), new QuotaMeaning("another-meaning"));
        var e = OpeningPolicy.Evaluate(first.NextBaseline, other);
        Assert.Equal(EvaluationOutcome.RevalidationRequired, e.Outcome);
        Assert.Same(first.NextBaseline, e.NextBaseline);
        Assert.Null(e.Opening);
    }

    [Fact]
    public void UnverifiedLabelRequiresRevalidationWithoutCreatingABaseline()
    {
        var o = SeatObservation.FromSource(Key, 1, Fixture.At(1), 1, 120, "surprise label");
        var e = OpeningPolicy.Evaluate(null, o);
        Assert.Equal(EvaluationOutcome.RevalidationRequired, e.Outcome);
        Assert.Null(e.NextBaseline);
        Assert.Null(e.Opening);
    }

    [Fact]
    public void ABaselineFromAnotherSectionIsRejected()
    {
        var other = OpeningPolicy.Evaluate(null, Fixture.Valid(Fixture.Key(section: "001"), 1, 120, 120)).NextBaseline;
        Assert.Throws<ArgumentException>(() => OpeningPolicy.Evaluate(other, Fixture.Valid(Key, 2, 119, 120)));
    }
}
