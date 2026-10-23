using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

/// <summary>镜头跟踪组合前景物品与出口中心，不把云层视差写成材料平移。</summary>
public sealed class SkyTopSceneMotionTests
{
    [Fact]
    public void SharedItemMotionCorrectsCameraEvenWithRepeatedWeaponsAndOneOutlier()
    {
        SkyTopDropVision.Drop[] before = [Item("枪械", 100, 200), Item("枪械", 180, 200), Item("弹药箱", 500, 300), Item("冷冻宝石", 600, 220)];
        var tracker = new SkyTopSceneMotion(); tracker.Observe(before);
        var moved = before.Select(item => item with { Bounds = item.Bounds with { X = item.Bounds.X + 100, Y = item.Bounds.Y - 40 } }).ToArray();
        moved[3] = moved[3] with { Bounds = moved[3].Bounds with { Y = moved[3].Bounds.Y + 90 } };
        Assert.Equal(new(100, -40, 1, 1), tracker.Observe(moved));
    }

    [Fact]
    public void IgnoredAmmoCanAnchorTheSceneWhenNoPickupTargetsRemain()
    {
        var tracker = new SkyTopSceneMotion(); tracker.Observe([Item("弹药箱", 300, 200)]);
        Assert.Equal(new(60, 30, 1, 1), tracker.Observe([Item("弹药箱", 360, 230)]));
        Assert.Equal(new(60, 30, 1, 1), tracker.Observe([]));
    }

    private static SkyTopDropVision.Drop Item(string name, int x, int y)
        => new(SkyTopDropVision.DropKind.Material, name, new(x, y, 20, 20), 1, false, false);

    [Fact]
    public void ExitTracksCameraAfterLastMaterialDisappears()
    {
        var tracker = new SkyTopSceneMotion();
        tracker.Observe(Scene(0, 0), [Item("材料", 300, 200)], new(300, 250, 12, 20), new(700, 400, 40, 40));
        var position = tracker.Observe(Scene(9, -4), [], new(380, 250, 12, 20), new(737, 381, 40, 40));
        Assert.True(position == new ScriptRect(37, -19, 1, 1), tracker.Registration + ":" + position);
        Assert.True(tracker.HasEstimate);
    }

    [Fact]
    public void CloudParallaxWithoutForegroundReferenceCannotRewriteLootCoordinates()
    {
        var tracker = new SkyTopSceneMotion();
        tracker.Observe(Scene(0, 0), [], null, null);
        Assert.Equal(new(0, 0, 1, 1), tracker.Observe(Scene(37, -19), [], null, null));
        Assert.False(tracker.HasEstimate);
    }

    [Fact]
    public void StationaryTextureCanBridgeAMissingExitAfterLastItemDisappears()
    {
        var tracker = new SkyTopSceneMotion();
        tracker.Observe(Scene(0, 0), [Item("材料", 300, 200)], null, new(700, 400, 40, 40));
        Assert.Equal(new(0, 0, 1, 1), tracker.Observe(Scene(0, 0), [], null, null));
        Assert.True(tracker.HasEstimate, tracker.Registration);
    }

    [Fact]
    public void FeaturelessSceneWithoutReferencesCannotInventCameraMovement()
    {
        var tracker = new SkyTopSceneMotion();
        var pixels = new ScriptPixels(950, 600, new byte[950 * 600 * 3]);
        tracker.Observe(pixels, [], null, null);
        tracker.Observe(pixels, [], null, null);
        Assert.False(tracker.HasEstimate);
    }

    // 非周期纹理按已知平移生成，两帧人物位置变化不参加配准。
    private static ScriptPixels Scene(int dx, int dy)
    {
        var bytes = new byte[950 * 600 * 3];
        for (var y = 0; y < 600; y++) for (var x = 0; x < 950; x++)
        {
            var wx = x - dx; var wy = y - dy;
            var hash = unchecked((uint)(wx / 11) * 0x9e3779b1u ^ (uint)(wy / 13) * 0x85ebca77u);
            hash ^= hash >> 16; hash *= 0x7feb352du; hash ^= hash >> 15;
            var value = (byte)(40 + (hash & 127));
            var at = (y * 950 + x) * 3;
            bytes[at] = value; bytes[at + 1] = (byte)(value + 10); bytes[at + 2] = (byte)(value + 20);
        }
        return new(950, 600, bytes);
    }
}
