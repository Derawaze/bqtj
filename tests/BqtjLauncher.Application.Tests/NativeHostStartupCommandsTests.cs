using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

public sealed class NativeHostStartupCommandsTests
{
    [Fact]
    public void OriginalSizeOnlySendsClientPixels()
    {
        var commands = NativeHostStartupCommands.Create(950, 600);

        Assert.Equal(["resize 950 600"], commands);
    }

    [Fact]
    public void ScaledSizeLeavesOpticalZoomToNativeViewport()
    {
        // 旧测试把冗余 scale 命令当成契约；真实倍率现在由原生夹具验证。
        var commands = NativeHostStartupCommands.Create(1425, 900);

        Assert.Equal(["resize 1425 900"], commands);
    }

    [Fact]
    public void ReloadRestoresClientPixelsWithoutSeparateScaleState()
    {
        var commands = NativeHostReloadCommands.Create(1425, 900);

        Assert.Equal(["reload", "resize 1425 900"], commands);
    }
}
