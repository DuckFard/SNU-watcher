using SnuWatcher.Infrastructure.Snu;
using Xunit;

namespace SnuWatcher.Integration.Tests;

public sealed class SourceIdentityValidationTests
{
    [Theory]
    [InlineData("012\n")]
    [InlineData("\u0660\u0661\u0662")]
    [InlineData("\uFF10\uFF11\uFF12")]
    public void SectionRejectsAnythingOtherThanExactlyThreeAsciiDigits(string section)
    {
        Assert.Throws<ArgumentException>(() => Identity(section));
    }

    [Fact]
    public void SectionPreservesThreeAsciiDigitsIncludingLeadingZero()
    {
        var identity = Identity("012");

        Assert.Equal("012", identity.Section);
    }

    private static SourceSectionIdentity Identity(string section) =>
        new(2026, "2", "1", "F32.102", section, "000");
}
