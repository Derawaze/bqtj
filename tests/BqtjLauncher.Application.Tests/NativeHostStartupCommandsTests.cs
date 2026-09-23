using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

public sealed class NativeHostStartupCommandsTests
{
    [Fact]
    public void OriginalScaleOnlyResizesAndDoesNotTriggerOpticalZoom()
    {
        var commands = NativeHostStartupCommands.Create(950, 600, 1m);

        Assert.Equal(["resize 950 600"], commands);
    }

    [Fact]
    public void ScaledModeResizesBeforeApplyingOpticalZoom()
    {
        var commands = NativeHostStartupCommands.Create(1425, 900, 1.5m);

        Assert.Equal(["resize 1425 900", "scale 150"], commands);
    }

    [Fact]
    public void ReloadResizesRecreatedBrowserBeforeReapplyingNonOriginalScale()
    {
        var commands = NativeHostReloadCommands.Create(1425, 900, 1.5m);

        Assert.Equal(["reload", "resize 1425 900", "scale 150"], commands);
    }
}
