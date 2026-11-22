using SnuWatcher.Application.Monitoring.Health;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Monitoring.Health;

public sealed class SectionHealthProjectorTests
{
    [Fact]
    public void NeverCheckedHasNoInventedTimesCountsOrAge()
    {
        var key = HealthTestData.Key();
        var health = SectionHealthProjector.Project(SectionSnapshot.Unchecked(key), HealthTestData.Now, TimeSpan.FromSeconds(30));
        Assert.Equal(key, health.Key);
        Assert.Equal(SectionDataState.NeverChecked, health.DataState);
        Assert.Equal(AvailabilityState.Unknown, health.Availability);
        Assert.Null(health.LastCompletedAttemptAtUtc);
        Assert.Null(health.LastSuccessfulAtUtc);
        Assert.Null(health.LastKnownCounts);
        Assert.Null(health.Age);
        Assert.False(health.IsStale);
    }

    [Theory]
    [InlineData(30, 120)]
    [InlineData(60, 120)]
    [InlineData(120, 240)]
    [InlineData(3600, 7200)]
    public void ThresholdIsTwiceTheTargetWithTwoMinuteMinimumAndInclusiveFreshBoundary(int target, int threshold)
    {
        var source = HealthTestData.Success(HealthTestData.Key(), at: HealthTestData.Now.AddSeconds(-threshold));
        var health = SectionHealthProjector.Project(source, HealthTestData.Now, TimeSpan.FromSeconds(target));
        Assert.Equal(TimeSpan.FromSeconds(threshold), health.StaleThreshold);
        Assert.Equal(SectionDataState.Current, health.DataState);
        Assert.Equal(AvailabilityState.Open, health.Availability);
        Assert.False(health.IsStale);
        var delayed = SectionHealthProjector.Project(source, HealthTestData.Now.AddTicks(1), TimeSpan.FromSeconds(target));
        Assert.Equal(SectionDataState.Delayed, delayed.DataState);
        Assert.True(delayed.IsStale);
        Assert.Equal(AvailabilityState.Unknown, delayed.Availability);
        Assert.Equal(source.Baseline!.Seats, delayed.LastKnownCounts);
    }

    [Theory]
    [InlineData(CheckOutcome.Unavailable)]
    [InlineData(CheckOutcome.RateLimited)]
    [InlineData(CheckOutcome.AccessRestricted)]
    [InlineData(CheckOutcome.ParseChanged)]
    [InlineData(CheckOutcome.NotFound)]
    [InlineData(CheckOutcome.Incomplete)]
    public void LatestFailureImmediatelyInvalidatesFreshAvailabilityButKeepsHistoricalCounts(CheckOutcome outcome)
    {
        var source = HealthTestData.Failure(HealthTestData.Key(), outcome);
        var health = SectionHealthProjector.Project(source, HealthTestData.Now, TimeSpan.FromSeconds(30));
        Assert.Equal(SectionDataState.Unavailable, health.DataState);
        Assert.Equal(AvailabilityState.Unknown, health.Availability);
        Assert.Equal(outcome, health.LatestOutcome);
        Assert.Equal(source.Baseline!.Seats, health.LastKnownCounts);
        Assert.Equal(source.LastSuccessfulAtUtc, health.LastSuccessfulAtUtc);
        Assert.Equal(source.LatestAttemptAtUtc, health.LastCompletedAttemptAtUtc);
        Assert.Equal(TimeSpan.FromSeconds(10), health.Age);
        Assert.False(health.IsStale);
    }

    [Fact]
    public void FailureCanAlsoHaveStaleHistoricalData()
    {
        var source = HealthTestData.Failure(HealthTestData.Key(), successfulAt: HealthTestData.Now.AddMinutes(-5));
        var health = SectionHealthProjector.Project(source, HealthTestData.Now, TimeSpan.FromSeconds(30));
        Assert.Equal(SectionDataState.Unavailable, health.DataState);
        Assert.True(health.IsStale);
        Assert.Equal(TimeSpan.FromMinutes(5), health.Age);
    }

    [Fact]
    public void FirstFailedCheckHasNoHistoricalCountsOrInventedAge()
    {
        var key = HealthTestData.Key();
        var source = new SectionSnapshot(key, 1, 1, CheckOutcome.Unavailable, HealthTestData.Now, null, null);
        var health = SectionHealthProjector.Project(source, HealthTestData.Now, TimeSpan.FromSeconds(30));
        Assert.Equal(SectionDataState.Unavailable, health.DataState);
        Assert.Equal(AvailabilityState.Unknown, health.Availability);
        Assert.Null(health.Age);
        Assert.Null(health.LastSuccessfulAtUtc);
        Assert.Null(health.LastKnownCounts);
    }

    [Fact]
    public void ClockRollbackProducesUnknownAgeForFutureSuccess()
    {
        var source = HealthTestData.Success(HealthTestData.Key(), at: HealthTestData.Now.AddMinutes(1));
        var health = SectionHealthProjector.Project(source, HealthTestData.Now, TimeSpan.FromSeconds(30));
        Assert.Equal(SectionDataState.ClockChanged, health.DataState);
        Assert.Null(health.Age);
        Assert.Equal(AvailabilityState.Unknown, health.Availability);
        Assert.Equal(source.Baseline!.Seats, health.LastKnownCounts);
    }

    [Fact]
    public void FutureLatestAttemptAlsoPreventsUsingAnOlderPastBaselineAsCurrent()
    {
        var source = HealthTestData.Failure(HealthTestData.Key(), attemptedAt: HealthTestData.Now.AddMinutes(1));
        var health = SectionHealthProjector.Project(source, HealthTestData.Now, TimeSpan.FromSeconds(30));
        Assert.Equal(SectionDataState.ClockChanged, health.DataState);
        Assert.Equal(TimeSpan.FromSeconds(10), health.Age);
        Assert.Equal(AvailabilityState.Unknown, health.Availability);
    }

    [Theory]
    [InlineData(0, 0, AvailabilityState.Full)]
    [InlineData(121, 120, AvailabilityState.Full)]
    [InlineData(0, 120, AvailabilityState.Open)]
    public void ZeroQuotaAndOverCapacityAreValidFullCounts(int registered, int capacity, AvailabilityState expected)
    {
        var health = SectionHealthProjector.Project(HealthTestData.Success(HealthTestData.Key(), registered, capacity),
            HealthTestData.Now, TimeSpan.FromSeconds(30));
        Assert.Equal(SectionDataState.Current, health.DataState);
        Assert.Equal(expected, health.Availability);
    }

    [Fact]
    public void UnverifiedQuotaMeaningCannotBecomeKnownAvailability()
    {
        var source = HealthTestData.Success(HealthTestData.Key(), meaning: new QuotaMeaning("unverified"));
        var health = SectionHealthProjector.Project(source, HealthTestData.Now, TimeSpan.FromSeconds(30));
        Assert.Equal(SectionDataState.Unavailable, health.DataState);
        Assert.Equal(AvailabilityState.Unknown, health.Availability);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(29.99)]
    [InlineData(3600.01)]
    public void InvalidRequestedTargetsFailAtTheBoundary(double seconds) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        SectionHealthProjector.Project(SectionSnapshot.Unchecked(HealthTestData.Key()), HealthTestData.Now, TimeSpan.FromSeconds(seconds)));
}
