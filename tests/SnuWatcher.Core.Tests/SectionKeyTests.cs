using Xunit;

namespace SnuWatcher.Core.Tests;

public sealed class SectionKeyTests
{
    [Fact]
    public void LeadingZerosSurviveAndAreNotNumeric()
    {
        Assert.Equal("012", Fixture.Key(section: "012").Section);
        Assert.Equal("F32.102(012)", Fixture.Key().ToString());
        Assert.False(Fixture.Key(section: "012") == Fixture.Key(section: "001"));
    }

    [Fact]
    public void EqualPartsGiveEqualKeys() => Assert.Equal(Fixture.Key(), Fixture.Key());

    [Theory]
    [InlineData("12")]
    [InlineData("0123")]
    [InlineData("01a")]
    [InlineData("012\n")]          // "$" would accept a trailing newline; \z does not
    [InlineData("\u0660\u0661\u0662")] // Arabic-Indic digits are not source section digits
    [InlineData("")]
    public void RejectsMalformedSections(string section) =>
        Assert.Throws<ArgumentException>(() => Fixture.Key(section: section));

    [Fact]
    public void RejectsNullSection() =>
        Assert.Throws<ArgumentException>(() => new SectionKey(2026, "A", "B", "C", null!, "0"));

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("a/b")]
    [InlineData("x\n")]
    public void RejectsMalformedTokens(string token) =>
        Assert.Throws<ArgumentException>(() => Fixture.Key(course: token));

    [Fact]
    public void RejectsTokenOverEightyCharacters() =>
        Assert.Throws<ArgumentException>(() => Fixture.Key(course: new string('A', 81)));

    [Theory]
    [InlineData(2012)]
    [InlineData(2201)]
    public void RejectsYearOutsideRange(int year) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SectionKey(year, "A", "B", "C", "001", "0"));
}
