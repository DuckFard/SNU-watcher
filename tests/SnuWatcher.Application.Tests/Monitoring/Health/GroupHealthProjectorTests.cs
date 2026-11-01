using SnuWatcher.Application.Monitoring.Health;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;
using Xunit;

namespace SnuWatcher.Application.Tests.Monitoring.Health;

public sealed class GroupHealthProjectorTests
{
    private static readonly TimeSpan Target = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(119, AvailabilityState.Open)]
    [InlineData(120, AvailabilityState.Unknown)]
    public void FreshOpenIsUsefulWithMissingMembersButPartialFullIsUnknown(int registered, AvailabilityState expected)
    {
        var a = HealthTestData.Key(); var b = HealthTestData.Key("013");
        var watch = HealthTestData.Watch([a, b]);
        var health = GroupHealthProjector.Project(watch, [HealthTestData.Success(a, registered)], HealthTestData.Now, Target);
        Assert.Equal(expected, health.Availability);
        Assert.Equal(1, health.CoverageCount);
        Assert.Equal(2, health.MemberCount);
        Assert.False(health.CoverageComplete);
        Assert.Equal(SectionDataState.NeverChecked, health.Members.Single(m => m.Key == b).DataState);
    }

    [Fact]
    public void FullRequiresFreshKnownFullForEveryMember()
    {
        var a = HealthTestData.Key(); var b = HealthTestData.Key("013");
        var watch = HealthTestData.Watch([a, b]);
        var full = GroupHealthProjector.Project(watch, [HealthTestData.Success(a, 120), HealthTestData.Success(b, 121)], HealthTestData.Now, Target);
        Assert.Equal(AvailabilityState.Full, full.Availability);
        Assert.True(full.CoverageComplete);
        var failed = GroupHealthProjector.Project(watch, [HealthTestData.Success(a, 120), HealthTestData.Failure(b)], HealthTestData.Now, Target);
        Assert.Equal(AvailabilityState.Unknown, failed.Availability);
        Assert.False(failed.CoverageComplete);
    }

    [Theory]
    [InlineData(WatchStatus.Active, WatchStatus.Expired)]
    [InlineData(WatchStatus.Paused, WatchStatus.Expired)]
    [InlineData(WatchStatus.Expired, WatchStatus.Expired)]
    [InlineData(WatchStatus.Removed, WatchStatus.Removed)]
    public void EffectiveExpiryIsIndependentOfCurrentCountsAndNeverResurrectsRemovedWatches(WatchStatus status, WatchStatus expected)
    {
        var key = HealthTestData.Key();
        var expiry = new WatchExpiry(new DateOnly(2026, 10, 31), "Asia/Seoul", HealthTestData.Now);
        var watch = HealthTestData.Watch([key], status, expiry);
        var health = GroupHealthProjector.Project(watch, [HealthTestData.Success(key)], HealthTestData.Now, Target);
        Assert.Equal(expected, health.EffectiveWatchStatus);
        Assert.Equal(status, watch.Status);
        Assert.Equal(AvailabilityState.Open, health.Availability);
    }

    [Fact]
    public void ProjectionCopiesInputsAndUsesExactCurrentMemberOrder()
    {
        var a = HealthTestData.Key(); var b = HealthTestData.Key("013");
        var watch = HealthTestData.Watch([b, a], WatchStatus.Paused);
        List<SectionSnapshot> states = [HealthTestData.Success(a), HealthTestData.Success(b, 120)];
        var health = GroupHealthProjector.Project(watch, states, HealthTestData.Now, Target);
        states.Clear();
        Assert.Equal(new[] { b, a }, health.Members.Select(m => m.Key));
        Assert.Equal(WatchStatus.Paused, health.EffectiveWatchStatus);
        Assert.Equal(2, health.CoverageCount);
        Assert.Equal(AvailabilityState.Open, health.Availability);
    }

    [Fact]
    public void DuplicateExtraOrUnboundedStatesAreRejected()
    {
        var a = HealthTestData.Key(); var state = HealthTestData.Success(a); var watch = HealthTestData.Watch([a]);
        Assert.Throws<ArgumentException>(() => GroupHealthProjector.Project(watch, [state, state], HealthTestData.Now, Target));
        Assert.Throws<ArgumentException>(() => GroupHealthProjector.Project(watch, [HealthTestData.Success(HealthTestData.Key("013"))], HealthTestData.Now, Target));
        Assert.Throws<ArgumentException>(() => GroupHealthProjector.Project(watch, Enumerable.Repeat(state, 2001), HealthTestData.Now, Target));
    }
}
