using SnuWatcher.Desktop.Presentation;
using Xunit;

namespace SnuWatcher.Windows.Tests;

public sealed class AboutVersionTests
{
    [Theory]
    [InlineData("0.1.0-preview.2+875e574a1b", "0.1.0-preview.2")]
    [InlineData("0.1.0-preview.2", "0.1.0-preview.2")]
    [InlineData(" 1.2.3+abc ", "1.2.3")]
    public void ShowsTheReleaseVersionWithoutBuildMetadata(string informational, string expected) =>
        Assert.Equal(expected, AboutVersion.Format(informational, new Version(9, 9, 9, 9)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("+abc")]
    public void FallsBackToTheAssemblyVersionWithoutAnInformationalVersion(string? informational)
    {
        Assert.Equal("0.1.0", AboutVersion.Format(informational, new Version(0, 1, 0, 0)));
        Assert.Equal("—", AboutVersion.Format(informational, null));
    }
}
