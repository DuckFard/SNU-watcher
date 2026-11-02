using Xunit;

namespace SnuWatcher.Core.Tests;

public sealed class WatchExpiryTests
{
    private static readonly TimeZoneInfo Seoul = TimeZoneInfo.CreateCustomTimeZone(
        "Test/Seoul", TimeSpan.FromHours(9), "Seoul", "KST");

    // Base offset 0. Spring-forward at local 00:00 on 10 March (00:00-01:00 does not exist);
    // fall-back at local 01:00 DST on 3 November (00:00-01:00 occurs twice).
    private static readonly TimeZoneInfo Quirky = TimeZoneInfo.CreateCustomTimeZone(
        "Test/Quirky", TimeSpan.Zero, "Quirky", "STD", "DST",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 10),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 11, 3)),
        ]);

    [Fact]
    public void SelectedDayIsIncludedAndNextDayStartIsExclusive()
    {
        var expiry = WatchExpiry.FromLocalDate(new DateOnly(2026, 11, 5), Seoul);

        Assert.Equal(new DateTimeOffset(2026, 11, 5, 15, 0, 0, TimeSpan.Zero), expiry.ExclusiveUtc);
        Assert.False(expiry.IsExpiredAt(new DateTimeOffset(2026, 11, 5, 14, 59, 59, TimeSpan.Zero)));  // 23:59:59 KST
        Assert.True(expiry.IsExpiredAt(new DateTimeOffset(2026, 11, 5, 15, 0, 0, TimeSpan.Zero)));     // 00:00 next day
        Assert.Equal("Test/Seoul", expiry.ZoneId);
    }

    [Fact]
    public void BoundaryIsStoredOnceAndDoesNotFollowTheHostZone()
    {
        var expiry = WatchExpiry.FromLocalDate(new DateOnly(2026, 11, 5), Seoul);
        var again = WatchExpiry.FromLocalDate(new DateOnly(2026, 11, 5), Seoul);
        Assert.Equal(expiry, again);
        Assert.Equal(new DateOnly(2026, 11, 5), expiry.LocalDate);
    }

    [Fact]
    public void DefaultZoneIsSeoul()
    {
        var expiry = WatchExpiry.FromLocalDate(new DateOnly(2026, 11, 5));
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 15, 0, 0, TimeSpan.Zero), expiry.ExclusiveUtc);
    }

    [Fact]
    public void NonexistentMidnightEndsAtTheTransitionInstant()
    {
        // 10 March 00:00 does not exist; the first valid local time is 01:00 DST = 00:00 UTC.
        var expiry = WatchExpiry.FromLocalDate(new DateOnly(2026, 3, 9), Quirky);
        Assert.Equal(new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero), expiry.ExclusiveUtc);
    }

    [Fact]
    public void GapAcrossMidnightEndsAtTheExactSecondRatherThanTheNextMinute()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 23, 59, 30), 3, 9),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 11, 3));
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Test/Seconds", TimeSpan.Zero, "Seconds", "STD", "DST", [rule]);

        // 23:59:30 jumps to 00:59:30 on the following day. That transition is its first real instant.
        var boundary = new DateTimeOffset(2026, 3, 9, 23, 59, 30, TimeSpan.Zero);
        var expiry = WatchExpiry.FromLocalDate(new DateOnly(2026, 3, 9), zone);

        Assert.Equal(boundary, expiry.ExclusiveUtc);
        Assert.False(expiry.IsExpiredAt(boundary.AddTicks(-1)));
        Assert.True(expiry.IsExpiredAt(boundary));
    }

    [Fact]
    public void RepeatedMidnightEndsAtItsFirstOccurrence()
    {
        // 3 November 00:00 occurs first in DST (23:00 UTC on the 2nd) and again in standard time.
        var expiry = WatchExpiry.FromLocalDate(new DateOnly(2026, 11, 2), Quirky);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 23, 0, 0, TimeSpan.Zero), expiry.ExclusiveUtc);
    }

    [Fact]
    public void LastRepresentableDateIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => WatchExpiry.FromLocalDate(DateOnly.MaxValue, Seoul));
}
