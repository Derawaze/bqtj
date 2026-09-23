using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

public sealed class GameWindowSizingTests
{
    [Theory]
    [InlineData(950, 600, 1, 1)]
    [InlineData(1425, 900, 1.5, 1.5)]
    [InlineData(1901, 1106, 2, 1.8433333333333333333333333333)]
    public void CalculateContainedScaleCapsRequestedScaleWithoutCropping(
        int clientWidth,
        int clientHeight,
        decimal requestedScale,
        decimal expectedScale)
    {
        var result = GameWindowSizing.CalculateContainedScale(
            clientWidth,
            clientHeight,
            nativeWidth: 950,
            nativeHeight: 600,
            requestedScale);

        Assert.InRange(
            Math.Abs(expectedScale - result),
            0m,
            0.00000000000001m);
    }

    [Theory]
    [InlineData(1000, 1478, 950, 1.5, 648)]
    [InlineData(690, 916, 600, 1.5, 479.3333333333333)]
    [InlineData(1000, 978, 950, 1.0, 972)]
    public void AdjustWindowDimensionAccountsForChromeAndDpi(
        double currentWindowDip,
        int currentClientPixels,
        int targetClientPixels,
        double dpiScale,
        double expectedWindowDip)
    {
        var result = GameWindowSizing.AdjustWindowDimension(
            currentWindowDip,
            currentClientPixels,
            targetClientPixels,
            dpiScale);

        Assert.Equal(expectedWindowDip, result, precision: 6);
    }

    [Theory]
    [InlineData(950, 950, 1900, 1.0, 1900)]
    [InlineData(600, 600, 1800, 1.0, 1800)]
    [InlineData(1000, 1478, 2850, 1.5, 1914.6666666666667)]
    public void AdjustWindowDimensionSupportsScaledGameTargets(
        double currentWindowDip,
        int currentClientPixels,
        int targetClientPixels,
        double dpiScale,
        double expectedWindowDip)
    {
        var result = GameWindowSizing.AdjustWindowDimension(
            currentWindowDip,
            currentClientPixels,
            targetClientPixels,
            dpiScale);

        Assert.Equal(expectedWindowDip, result, precision: 6);
    }
}

public sealed class GameWindowPlacementTests
{
    [Theory]
    [InlineData(0, 0, 1920, 1080, 1000, 700, 1, 1, 460, 190)]
    [InlineData(1920, 0, 2560, 1440, 1000, 700, 1, 1, 2700, 370)]
    [InlineData(0, 0, 1920, 1080, 1000, 700, 1.5, 1.5, 140, 10)]
    [InlineData(0, 0, 1920, 1080, 2000, 1200, 1, 1, -40, -60)]
    public void CenterInWorkAreaCentersEvenWhenWindowExceedsScreen(
        int workLeft,
        int workTop,
        int workWidth,
        int workHeight,
        double windowWidth,
        double windowHeight,
        double dpiX,
        double dpiY,
        double expectedLeft,
        double expectedTop)
    {
        var result = GameWindowPlacement.CenterInWorkArea(
            workLeft,
            workTop,
            workWidth,
            workHeight,
            windowWidth,
            windowHeight,
            dpiX,
            dpiY);

        Assert.Equal(expectedLeft, result.Left, precision: 6);
        Assert.Equal(expectedTop, result.Top, precision: 6);
    }
}
