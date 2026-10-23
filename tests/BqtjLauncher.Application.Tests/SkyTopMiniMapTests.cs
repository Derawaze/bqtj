using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

/// <summary>角色黄点与队友绿点分开，波次文字下沿附近的标记也必须纳入识别。</summary>
public sealed class SkyTopMiniMapTests
{
    [Fact]
    public void YellowPlayerAtTheWaveTextBottomIsDistinctFromGreenTeammates()
    {
        var region = new ScriptRect(831, 21, 74, 44);
        var frame = Frame([(850, 42), (870, 36)], []);
        for (var y = 20; y < 23; y++) for (var x = 902; x < 905; x++)
        { var at = (y * 950 + x) * 3; frame.Bgr[at + 1] = 255; frame.Bgr[at + 2] = 255; }
        Assert.Equal(new SkyTopMiniMap.Dot(903, 22), SkyTopMiniMap.FindPlayer(frame, region));
        Assert.Null(SkyTopMiniMap.FindPlayer(Frame([(850, 42)], []), region));
    }

    private static readonly ScriptRect Region = new(810, 25, 116, 41);
    [Fact]
    public void StationaryTeammatesCannotBeChosenAsThePlayer()
    {
        var map = new SkyTopMiniMap(Region);
        var frame = Frame([(820, 55), (830, 45)], [(870, 30)]);
        map.ObserveEnemies(frame, true);
        Assert.Null(map.Heading(frame));
        Assert.Null(map.Heading(Frame([(822, 53), (832, 43)], [])));
    }

    [Fact]
    public void UniqueYellowPlayerUsesLastEnemyPositionAfterRedDotsDisappear()
    {
        var map = new SkyTopMiniMap(Region);
        var frame = Frame([(820, 55), (830, 45)], [(870, 30)]);
        map.ObserveEnemies(frame, true);
        var after = Frame([(830, 45)], [], (822, 53));
        map.ObserveEnemies(after, true);
        Assert.Equal(('W', (char?)'D'), map.Heading(after));
        Assert.Equal(('D', (char?)null), map.Heading(Frame([(830, 45)], [], (824, 31))));
    }

    [Fact]
    public void EarlierWaveAndMissingYellowPlayerCannotGuideSearch()
    {
        var map = new SkyTopMiniMap(Region);
        var frame = Frame([(820, 55)], [(870, 30)], (822, 53));
        map.ObserveEnemies(frame, false);
        Assert.Null(map.Heading(frame));
        map.ObserveEnemies(frame, true);
        Assert.Null(map.Heading(Frame([(818, 57)], [])));
    }

    private static ScriptPixels Frame((int X, int Y)[] greens, (int X, int Y)[] reds, (int X, int Y)? yellow = null)
    {
        var pixels = new byte[950 * 600 * 3];
        foreach (var dot in greens) pixels[(dot.Y * 950 + dot.X) * 3 + 1] = 255;
        foreach (var dot in reds) pixels[(dot.Y * 950 + dot.X) * 3 + 2] = 255;
        if (yellow is { } player)
            for (var y = player.Y - 1; y <= player.Y + 1; y++) for (var x = player.X - 1; x <= player.X + 1; x++)
            { pixels[(y * 950 + x) * 3 + 1] = 255; pixels[(y * 950 + x) * 3 + 2] = 255; }
        return new(950, 600, pixels);
    }
}
