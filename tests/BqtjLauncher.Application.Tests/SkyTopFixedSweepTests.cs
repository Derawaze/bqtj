using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

/// <summary>上沿路线按边界观察提前停键；补捡只覆盖当前可见区域，并用实际位移换算时长。</summary>
public sealed class SkyTopFixedSweepTests
{
    [Theory]
    [InlineData(.5, .5)]
    [InlineData(1, .8)]
    public void LocalPickupMakesOnePassWithTwentyPercentMoreHorizontalCoverage(double speedX, double speedY)
    {
        var settings = new SkyTopFixedSweep.Settings(1500, 3500, 38, 180);
        var route = SkyTopFixedSweep.Build(new(294, 90, 12, 20), settings, speedX, speedY, new(300, 100, 120, 76));
        var directions = new List<char>();
        foreach (var step in route)
        {
            Assert.Null(step.Second);
            if (directions.LastOrDefault() != step.Key) directions.Add(step.Key);
        }
        Assert.Equal("DSA", new string(directions.ToArray()));
        Assert.InRange(route.Where(step => step.Key == 'D').Sum(step => step.Milliseconds) * speedX, 287, 289);
        Assert.InRange(route.Where(step => step.Key == 'S').Sum(step => step.Milliseconds) * speedY, 37, 39);
        Assert.InRange(route.Where(step => step.Key == 'A').Sum(step => step.Milliseconds) * speedX, 287, 289);
    }

    [Fact]
    public void BloodStoneOnlyExtendsANearbyRealMaterialArea()
    {
        var blood = new SkyTopDropVision.Drop(SkyTopDropVision.DropKind.BloodStone, "血石", new(335, 210, 20, 20), 1, false, true);
        Assert.Null(SkyTopFixedSweep.PickupArea([blood]));
        var gem = new SkyTopDropVision.Drop(SkyTopDropVision.DropKind.GuardGem, "守护宝石", new(300, 200, 20, 20), 1, false, true);
        var farBlood = blood with { Bounds = new(700, 400, 20, 20) };
        Assert.Equal(new BqtjLauncher.Application.ScriptRect(280, 180, 95, 70), SkyTopFixedSweep.PickupArea([gem, blood, farBlood]));
    }
    [Fact]
    public void BoundaryNeedsAnAcknowledgedMoveAtTheVisibleEdgeAndNoCameraScroll()
    {
        Assert.True(SkyTopFixedSweep.AtBoundary(new(67, 47, 12, 20), new(700, 390, 40, 24),
            new(67, 47, 12, 20), new(700, 391, 40, 24), false));
        Assert.True(SkyTopFixedSweep.AtBoundary(new(911, 47, 12, 20), null, new(911, 47, 12, 20), null, true));
        Assert.False(SkyTopFixedSweep.AtBoundary(new(500, 200, 12, 20), null, new(500, 200, 12, 20), null, true));
        Assert.False(SkyTopFixedSweep.AtBoundary(new(67, 47, 12, 20), new(700, 390, 40, 24),
            new(67, 47, 12, 20), new(700, 420, 40, 24), false));
        Assert.False(SkyTopFixedSweep.AtBoundary(null, null, new(67, 47, 12, 20), null, false));
        Assert.False(SkyTopFixedSweep.AtBoundary(new(911, 47, 12, 20), null, null, null, true));
    }

    [Theory]
    [InlineData(.3, .4)]
    [InlineData(.5, .5)]
    [InlineData(1, .8)]
    public void ApproachThenOneLocalPassEndsAtTheLeftSide(double speedX, double speedY)
    {
        var settings = new SkyTopFixedSweep.Settings(2000, 2000, 38, 180);
        var pulses = SkyTopFixedSweep.Build(new(294, 290, 12, 20), settings, speedX, speedY, new(305, 55, 310, 165));
        var x = 300d; var y = 300d;
        foreach (var pulse in pulses)
        {
            Assert.InRange(pulse.Milliseconds, 1, 180);
            Apply(pulse.Key); if (pulse.Second is { } second) Apply(second);
            void Apply(char key)
            {
                x += key == 'D' ? pulse.Milliseconds * speedX : key == 'A' ? -pulse.Milliseconds * speedX : 0;
                y += key == 'S' ? pulse.Milliseconds * speedY : key == 'W' ? -pulse.Milliseconds * speedY : 0;
            }
        }
        // 极短轴差略过，容差仍小于拾取半径，避免为几像素额外倒车。
        Assert.InRange(x, 295, 315); Assert.InRange(y, 80, 98);
        Assert.InRange(pulses.Sum(pulse => pulse.Milliseconds), 1500, 10000);
    }

    [Fact]
    public void CalibrationSubtractsCameraMovementAndRejectsNoMovement()
    {
        Assert.Equal(.5, SkyTopFixedSweep.Speed(new(100, 100, 12, 20), new(500, 350, 30, 30),
            new(120, 100, 12, 20), new(470, 350, 30, 30), true, 100));
        Assert.Throws<InvalidOperationException>(() => SkyTopFixedSweep.Speed(new(100, 100, 12, 20), new(500, 350, 30, 30),
            new(100, 100, 12, 20), new(500, 350, 30, 30), true, 100));
    }
}
