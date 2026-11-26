using SnuWatcher.Windows.Updates;
using Xunit;

namespace SnuWatcher.Windows.Tests.Updates;

public sealed class ReleaseVersionTests
{
    private static ReleaseVersion Parse(string text) =>
        ReleaseVersion.TryParse(text, out var version) ? version! : throw new Xunit.Sdk.XunitException("Not a version: " + text);

    [Theory]
    [InlineData("0.1.0-preview.3", "0.1.0-preview.3")]
    [InlineData("v0.1.0-preview.3", "0.1.0-preview.3")]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("v12.0.7", "12.0.7")]
    public void PublishedTagsAndAppVersionsParse(string text, string canonical)
    {
        Assert.Equal(canonical, Parse(text).ToString());
        Assert.Equal("v" + canonical, Parse(text).Tag);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.1")]
    [InlineData("0.1.0.2")]
    [InlineData("0.1.0-beta.1")]
    [InlineData("0.1.0-preview")]
    [InlineData("0.1.0-preview.3+582af5e")]
    [InlineData("01.1.0")]
    [InlineData("0.1.0-preview.03")]
    [InlineData("V0.1.0")]
    [InlineData(" 0.1.0")]
    [InlineData("0.1.0\n")]
    [InlineData("100000.0.0")]
    public void AnythingElseIsNotAVersion(string? text)
    {
        Assert.False(ReleaseVersion.TryParse(text, out var version));
        Assert.Null(version);
    }

    [Theory]
    [InlineData("0.1.0-preview.2", "0.1.0-preview.3")]
    [InlineData("0.1.0-preview.9", "0.1.0-preview.10")]
    [InlineData("0.1.0-preview.3", "0.1.0")]
    [InlineData("0.1.0", "0.1.1-preview.1")]
    [InlineData("0.1.9", "0.2.0")]
    [InlineData("0.9.0", "1.0.0-preview.1")]
    public void VersionsOrderAsReleasesDo(string older, string newer)
    {
        Assert.True(Parse(older) < Parse(newer));
        Assert.True(Parse(newer) > Parse(older));
        Assert.True(Parse(older).CompareTo(Parse(newer)) < 0);
    }

    [Fact]
    public void EqualVersionsAreEqual()
    {
        Assert.Equal(Parse("v0.1.0-preview.3"), Parse("0.1.0-preview.3"));
        Assert.Equal(0, Parse("1.0.0").CompareTo(Parse("1.0.0")));
        Assert.False(Parse("1.0.0") > Parse("1.0.0"));
        Assert.True(Parse("1.0.0").CompareTo(null) > 0);
    }
}
