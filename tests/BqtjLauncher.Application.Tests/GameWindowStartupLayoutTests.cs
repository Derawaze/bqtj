using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

public sealed class GameWindowStartupLayoutTests
{
    [Theory]
    [InlineData((int)GameWindowMode.Original, false, 1, 1, 1000, 690)]
    [InlineData((int)GameWindowMode.Scale150, false, 1.5, 1.5, 1475, 990)]
    [InlineData((int)GameWindowMode.Scale200, false, 2, 2, 1950, 1290)]
    [InlineData((int)GameWindowMode.FullScreen, true, 1, 1, 1000, 690)]
    public void SavedModeDeterminesGeometryBeforeFirstShow(
        int windowMode,
        bool expectedFullScreen,
        decimal expectedRequestedScale,
        decimal expectedAppliedScale,
        double expectedWidth,
        double expectedHeight)
    {
        var result = GameWindowStartupLayout.Calculate(
            (GameWindowMode)windowMode,
            originalWindowWidth: 1000,
            originalWindowHeight: 690,
            workAreaWidth: 2560,
            workAreaHeight: 1440,
            dpiScaleX: 1,
            dpiScaleY: 1);

        Assert.Equal(expectedFullScreen, result.IsFullScreen);
        Assert.Equal(expectedRequestedScale, result.RequestedScale);
        Assert.Equal(expectedAppliedScale, result.AppliedScale);
        Assert.Equal(expectedWidth, result.Width, precision: 6);
        Assert.Equal(expectedHeight, result.Height, precision: 6);
    }

    [Fact]
    public void OversizedModeIsContainedInWorkAreaBeforeFirstShow()
    {
        var result = GameWindowStartupLayout.Calculate(
            GameWindowMode.Scale200,
            originalWindowWidth: 1000,
            originalWindowHeight: 690,
            workAreaWidth: 1920,
            workAreaHeight: 1080,
            dpiScaleX: 1,
            dpiScaleY: 1);

        Assert.Equal(2m, result.RequestedScale);
        Assert.Equal(1.65m, result.AppliedScale);
        Assert.Equal(1617.5, result.Width, precision: 6);
        Assert.Equal(1080, result.Height, precision: 6);
    }

    [Fact]
    public void InitialGeometryConvertsPhysicalGamePixelsToDipsAtHighDpi()
    {
        var result = GameWindowStartupLayout.Calculate(
            GameWindowMode.Original,
            originalWindowWidth: 1000,
            originalWindowHeight: 690,
            workAreaWidth: 2560,
            workAreaHeight: 1440,
            dpiScaleX: 1.5,
            dpiScaleY: 1.5);

        Assert.Equal(1m, result.AppliedScale);
        Assert.Equal(683.3333333333334, result.Width, precision: 6);
        Assert.Equal(490, result.Height, precision: 6);
    }
}
