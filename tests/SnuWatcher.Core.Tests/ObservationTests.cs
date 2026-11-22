using Xunit;

namespace SnuWatcher.Core.Tests;

public sealed class ObservationTests
{
    [Theory]
    [InlineData("수강신청인원/정원(재학생)")]
    [InlineData(" 수강신청인원 / 정원(재학생) ")]
    [InlineData("Enrollment/Quota")]
    [InlineData("enrollment / quota")]
    public void KnownLabelsMapToOneMeaning(string label) =>
        Assert.Equal(QuotaMeaning.EnrolledOverQuota, QuotaLabels.Map(label));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("총수강인원")]
    [InlineData("'; DROP TABLE watches; --")]
    public void UnknownLabelsAreNotGuessed(string? label) => Assert.Null(QuotaLabels.Map(label));

    [Fact]
    public void FromSourceBuildsValidObservation()
    {
        var o = SeatObservation.FromSource(Fixture.Key(), 3, Fixture.At(3), 119, 120, "Enrollment/Quota");
        Assert.Equal(ObservationKind.Valid, o.Kind);
        Assert.Equal(1, o.Seats!.Value.ApparentSeats);
    }

    [Theory]
    [InlineData(null, 120)]
    [InlineData(119, null)]
    [InlineData(-1, 120)]
    [InlineData(119, -5)]
    public void MissingOrNegativeCountsAreUnknown(int? registered, int? capacity)
    {
        var o = SeatObservation.FromSource(Fixture.Key(), 3, Fixture.At(3), registered, capacity, "Enrollment/Quota");
        Assert.Equal(ObservationKind.Unknown, o.Kind);
        Assert.Null(o.Seats);
    }

    [Fact]
    public void UnmappedLabelIsSemanticsUnverifiedNotValid()
    {
        var o = SeatObservation.FromSource(Fixture.Key(), 3, Fixture.At(3), 1, 120, "something new");
        Assert.Equal(ObservationKind.SemanticsUnverified, o.Kind);
        Assert.Null(o.Meaning);
    }

    [Theory]
    [InlineData(119, 120, 1)]
    [InlineData(120, 120, 0)]
    [InlineData(130, 120, 0)]  // over capacity
    [InlineData(0, 0, 0)]      // zero capacity
    [InlineData(0, 40, 40)]
    public void ApparentSeatsNeverGoNegative(int registered, int capacity, int expected) =>
        Assert.Equal(expected, new SeatCount(registered, capacity).ApparentSeats);

    [Fact]
    public void SeatCountRejectsNegativeInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SeatCount(-1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SeatCount(1, -10));
    }
}
