using SnuWatcher.Windows;
using Xunit;

namespace SnuWatcher.Windows.Tests;

public sealed class WindowSizePolicyTests
{
    [Theory]
    [InlineData(1536, 864, 1060, 720, 800, 580)]
    [InlineData(1024, 592, 1024, 592, 800, 580)]
    [InlineData(900, 800, 900, 720, 800, 580)]
    [InlineData(1600, 600, 1060, 600, 800, 580)]
    [InlineData(640, 480, 640, 480, 640, 480)]
    [InlineData(1000.5, 650.25, 1000.5, 650.25, 800, 580)]
    [InlineData(1060, 720, 1060, 720, 800, 580)]
    public void InitialWindowFitsAvailableDesktopWithoutDiscardingUsableMinimums(
        double availableWidth, double availableHeight, double width, double height,
        double minimumWidth, double minimumHeight)
    {
        var size = WindowSizePolicy.FitToWorkArea(availableWidth, availableHeight, 1060, 720, 800, 580);

        Assert.Equal(new InitialWindowSize(width, height, minimumWidth, minimumHeight), size);
    }

    [Fact]
    public void PreferencesBelowMinimumsProduceAUsableWindow()
    {
        var size = WindowSizePolicy.FitToWorkArea(1536, 864, 700, 500, 800, 580);

        Assert.Equal(new InitialWindowSize(800, 580, 800, 580), size);
    }

    public static IEnumerable<object[]> InvalidDimensions =>
        from index in Enumerable.Range(0, 6)
        from value in new[] { 0d, -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity }
        select new object[] { index, value };

    [Theory]
    [MemberData(nameof(InvalidDimensions))]
    public void InvalidDimensionsAreRejectedAtTheBoundary(int index, double value)
    {
        var dimensions = new[] { 1536d, 864d, 1060d, 720d, 800d, 580d }
            .Select((dimension, position) => position == index ? value : dimension).ToArray();

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => WindowSizePolicy.FitToWorkArea(
            dimensions[0], dimensions[1], dimensions[2], dimensions[3], dimensions[4], dimensions[5]));

        Assert.Equal(new[] { "availableWidth", "availableHeight", "preferredWidth", "preferredHeight", "minimumWidth", "minimumHeight" }[index], error.ParamName);
    }
}
