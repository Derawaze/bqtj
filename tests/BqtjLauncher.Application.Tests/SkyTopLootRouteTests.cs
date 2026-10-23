using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

/// <summary>以可观察的移动和完成行为验证整带拾取，不读取真实账号或操控游戏。</summary>
public sealed class SkyTopLootRouteTests
{
    private static readonly ScriptRect Camera = new(0, 0, 1, 1);
    private static ScriptRect Hero(int x, int y) => new(x - 6, y - 10, 12, 20);
    private static SkyTopSceneVision.LootGroup Item(int x, int y, bool uncertain = false)
        => new(new(x - 10, y - 10, 20, 20), uncertain, "材料");

    [Fact]
    public void ShrinkingOverlappingPileDoesNotLeaveItsOldCentreAsAGhostBand()
    {
        var route = new SkyTopLootRoute();
        // 同一堆晶体被收走一部分后，外框中心会明显变化，不能凭中心距离登记成第二堆。
        route.Observe(Hero(430, 350), Camera, [new(new(300, 190, 140, 20), true, "材料")], TimeSpan.Zero);
        route.Observe(Hero(430, 260), Camera, [Item(430, 200)], TimeSpan.FromMilliseconds(200));
        route.Observe(Hero(430, 200), Camera, [], TimeSpan.FromMilliseconds(400));
        route.Observe(Hero(430, 100), Camera, [], TimeSpan.FromMilliseconds(600));
        Assert.True(route.Observe(Hero(430, 100), Camera, [], TimeSpan.FromMilliseconds(800)).Complete);
    }

    [Fact]
    public void WidePileGetsEnoughClearanceToConfirmPickupWithoutRepeatedlyReenteringTheBand()
    {
        var route = new SkyTopLootRoute();
        var pile = new SkyTopSceneVision.LootGroup(new(330, 200, 140, 20), true, "材料");
        var x = 200; var collected = false; var complete = false;
        for (var step = 0; step < 25; step++)
        {
            var move = route.Observe(Hero(x, 210), Camera, collected ? [] : [pile], TimeSpan.FromMilliseconds(step * 200));
            if (move.Complete) { complete = true; break; }
            var before = x;
            if (move.Key == 'D') x += move.Milliseconds / 2;
            else if (move.Key == 'A') x -= move.Milliseconds / 2;
            if (Math.Min(before, x) <= 432 && Math.Max(before, x) >= 368) collected = true;
        }
        Assert.True(collected && complete);
    }

    [Theory]
    [InlineData(200, 300, 'W', 'D')]
    [InlineData(400, 300, 'W', 'A')]
    [InlineData(200, 100, 'S', 'D')]
    [InlineData(400, 100, 'S', 'A')]
    public void ApproachesBandDiagonally(int x, int y, char first, char second)
    {
        var move = new SkyTopLootRoute().Observe(Hero(x, y), Camera, [Item(310, 210)], TimeSpan.Zero);
        Assert.Equal(first, move.Key); Assert.Equal(second, move.SecondKey);
        Assert.InRange(move.Milliseconds, 30, 140);
    }

    [Fact]
    public void SweepsContinuousBandWithoutStoppingAtEachItemOrWaitingThreeSeconds()
    {
        var route = new SkyTopLootRoute();
        Assert.Equal('D', route.Observe(Hero(200, 210), Camera, [Item(300, 210), Item(380, 210), Item(460, 210)], TimeSpan.Zero).Key);
        Assert.Equal('D', route.Observe(Hero(300, 210), Camera, [Item(380, 210), Item(460, 210)], TimeSpan.FromMilliseconds(150)).Key);
        Assert.Equal('D', route.Observe(Hero(400, 210), Camera, [Item(460, 210)], TimeSpan.FromMilliseconds(300)).Key);
        Assert.True(route.Observe(Hero(540, 210), Camera, [], TimeSpan.FromMilliseconds(450)).Complete);
    }

    [Fact]
    public void StartsWithNearerEndWhenHeroIsInMiddle()
    {
        var route = new SkyTopLootRoute();
        Assert.Equal('D', route.Observe(Hero(450, 210), Camera, [Item(300, 210), Item(400, 210), Item(500, 210)], TimeSpan.Zero).Key);
        Assert.Equal('A', route.Observe(Hero(500, 210), Camera, [Item(300, 210), Item(400, 210)], TimeSpan.FromMilliseconds(200)).Key);
    }

    [Fact]
    public void SlantedNearbyItemsRemainOneBand()
    {
        var route = new SkyTopLootRoute();
        route.Observe(Hero(200, 180), Camera, [Item(300, 200), Item(380, 220), Item(460, 240)], TimeSpan.Zero);
        var sweep = route.Observe(Hero(300, 200), Camera, [Item(380, 220), Item(460, 240)], TimeSpan.FromMilliseconds(200));
        Assert.Equal('S', sweep.Key); Assert.Equal('D', sweep.SecondKey);
    }

    [Fact]
    public void SeparateLowerDropIsVisitedAfterUpperGroupHasBeenPassed()
    {
        var route = new SkyTopLootRoute();
        route.Observe(Hero(200, 200), Camera, [Item(300, 200), Item(300, 370)], TimeSpan.Zero);
        route.Observe(Hero(300, 200), Camera, [Item(300, 370)], TimeSpan.FromMilliseconds(200));
        var lower = route.Observe(Hero(380, 200), Camera, [Item(300, 370)], TimeSpan.FromMilliseconds(400));
        Assert.Equal('S', lower.Key); Assert.Equal('A', lower.SecondKey); Assert.False(lower.Complete);
    }

    [Fact]
    public void DisappearanceWithoutActuallyPassingCannotConfirmPickup()
    {
        var route = new SkyTopLootRoute();
        route.Observe(Hero(100, 210), Camera, [Item(300, 210)], TimeSpan.Zero);
        var missing = route.Observe(Hero(100, 210), Camera, [], TimeSpan.FromSeconds(2));
        Assert.False(missing.Complete); Assert.Equal('D', missing.Key);
        Assert.Throws<InvalidOperationException>(() => route.Observe(Hero(100, 210), Camera, [], TimeSpan.FromSeconds(9)));
    }

    [Fact]
    public void MaterialUnderHeroOrAnotherItemIsNotConfirmed()
    {
        var route = new SkyTopLootRoute();
        route.Observe(Hero(200, 210), Camera, [Item(300, 210)], TimeSpan.Zero);
        Assert.False(route.Observe(Hero(300, 210), Camera, [], TimeSpan.FromMilliseconds(200)).Complete);
        Assert.False(route.Observe(Hero(380, 210), Camera, [], TimeSpan.FromMilliseconds(400), [new(290, 200, 20, 20)]).Complete);
    }

    [Fact]
    public void OffscreenPendingMaterialSurvivesCameraShift()
    {
        var route = new SkyTopLootRoute();
        route.Observe(Hero(200, 210), Camera, [Item(300, 210), Item(800, 210)], TimeSpan.Zero);
        route.Observe(Hero(300, 210), Camera, [], TimeSpan.FromMilliseconds(200));
        var shifted = route.Observe(Hero(280, 210), new(-100, 0, 1, 1), [], TimeSpan.FromMilliseconds(400));
        Assert.False(shifted.Complete); Assert.Equal('D', shifted.Key);
    }

    [Fact]
    public void UnconfirmedCameraCannotRetireItemsOrSendNewMovement()
    {
        var route = new SkyTopLootRoute();
        route.Observe(Hero(200, 210), Camera, [Item(300, 210)], TimeSpan.Zero);
        var uncertain = route.Observe(Hero(380, 210), Camera, [], TimeSpan.FromMilliseconds(200), sceneReliable: false);
        Assert.False(uncertain.Complete); Assert.Null(uncertain.Key);
    }

    [Fact]
    public void NewMaterialRevealedDuringFinalPassIsAppended()
    {
        var route = new SkyTopLootRoute();
        route.Observe(Hero(200, 210), Camera, [Item(300, 210)], TimeSpan.Zero);
        route.Observe(Hero(300, 210), Camera, [], TimeSpan.FromMilliseconds(150));
        var revealed = route.Observe(Hero(380, 210), Camera, [Item(500, 210)], TimeSpan.FromMilliseconds(300));
        Assert.False(revealed.Complete); Assert.Equal('D', revealed.Key);
    }

    [Fact]
    public void OverlappingTargetGetsOneAdditionalClearFrame()
    {
        var route = new SkyTopLootRoute();
        route.Observe(Hero(200, 210), Camera, [Item(300, 210, true)], TimeSpan.Zero);
        route.Observe(Hero(300, 210), Camera, [], TimeSpan.FromMilliseconds(150));
        var first = route.Observe(Hero(380, 210), Camera, [], TimeSpan.FromMilliseconds(300));
        Assert.False(first.Complete); Assert.Null(first.Key);
        Assert.True(route.Observe(Hero(380, 210), Camera, [], TimeSpan.FromMilliseconds(450)).Complete);
    }

    [Fact]
    public void EmptyRoundNeedsAreaEvidenceAndTwoFreshInspections()
    {
        var route = new SkyTopLootRoute();
        Assert.False(route.Observe(Hero(200, 210), Camera, [], TimeSpan.Zero, areaObserved: false).Complete);
        Assert.False(route.Observe(Hero(200, 210), Camera, [], TimeSpan.FromSeconds(4), areaObserved: true).Complete);
        Assert.True(route.Observe(Hero(200, 210), Camera, [], TimeSpan.FromMilliseconds(4150), areaObserved: true).Complete);
    }

    [Fact]
    public void OvershootingEntryDoesNotTurnBackToAnAlreadyPassedPoint()
    {
        var route = new SkyTopLootRoute();
        route.Observe(Hero(200, 210), Camera, [Item(300, 210), Item(450, 210)], TimeSpan.Zero);
        var beyond = route.Observe(Hero(340, 210), Camera, [Item(450, 210)], TimeSpan.FromMilliseconds(200));
        Assert.Equal('D', beyond.Key);
    }

    [Fact]
    public void RejectedInputCannotProvidePickupEvidenceOnResume()
    {
        var route = new SkyTopLootRoute();
        route.Observe(Hero(200, 210), Camera, [Item(300, 210)], TimeSpan.Zero);
        route.InputRejected();
        Assert.False(route.Observe(Hero(380, 210), Camera, [], TimeSpan.FromMilliseconds(200)).Complete);
    }

    [Fact]
    public void RevealRecoveryKeepsAlreadyPassedMaterialEvidenceForFinalInspection()
    {
        var route = new SkyTopLootRoute();
        route.Observe(Hero(200, 200), Camera, [Item(300, 200)], TimeSpan.Zero);
        route.Observe(Hero(280, 200), Camera, [], TimeSpan.FromMilliseconds(150));
        // 血条丢失期间不伪造新移动证据，但已经进入拾取范围的事实和清单仍保留。
        route.InputRejected();
        Assert.True(route.Observe(Hero(380, 220), new(0, -80, 1, 1), [], TimeSpan.FromMilliseconds(600)).Complete);
    }

    [Theory]
    [InlineData(.35)]
    [InlineData(.8)]
    [InlineData(1.2)]
    public void CompleteTwoBandsWithCameraChangesAndDifferentActualSpeeds(double speed)
    {
        var route = new SkyTopLootRoute();
        var remaining = new List<(double X, double Y)> { (400, 210), (500, 220), (600, 230), (450, 400), (550, 410) };
        double x = 200, y = 300, distance = 0;
        var complete = false;
        for (var step = 0; step < 100; step++)
        {
            // 镜头跟随人物，转向时额外偏移；清单坐标不应跟着屏幕坐标漂移。
            var cameraX = (int)Math.Min(0, 550 - x); var cameraY = (int)Math.Clamp(300 - y, -70, 70);
            var groups = remaining.Where(item => item.X + cameraX is > 10 and < 930 && item.Y + cameraY is > 85 and < 470)
                .Where(item => Math.Abs(item.X - x) > 65 || Math.Abs(item.Y - y) > 65)
                .Select(item => Item((int)(item.X + cameraX), (int)(item.Y + cameraY))).ToArray();
            var move = route.Observe(Hero((int)(x + cameraX), (int)(y + cameraY)), new(cameraX, cameraY, 1, 1), groups,
                TimeSpan.FromMilliseconds(step * 250));
            if (move.Complete) { complete = true; break; }
            if (move.Key is null) continue;
            var beforeX = x; var beforeY = y;
            var amount = speed * move.Milliseconds * (move.SecondKey is null ? 1 : .707);
            Apply(move.Key.Value); if (move.SecondKey is { } second) Apply(second);
            distance += Math.Sqrt(Math.Pow(x - beforeX, 2) + Math.Pow(y - beforeY, 2));
            remaining.RemoveAll(item => SegmentDistance(item.X, item.Y, beforeX, beforeY, x, y) <= 32);
            void Apply(char key) { if (key == 'D') x += amount; if (key == 'A') x -= amount; if (key == 'S') y += amount; if (key == 'W') y -= amount; }
        }
        Assert.True(complete); Assert.Empty(remaining); Assert.InRange(distance, 600, 1400);
    }

    private static double SegmentDistance(double x, double y, double ax, double ay, double bx, double by)
    {
        var dx = bx - ax; var dy = by - ay;
        var t = Math.Clamp(((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy), 0, 1);
        return Math.Sqrt(Math.Pow(x - ax - dx * t, 2) + Math.Pow(y - ay - dy * t, 2));
    }
}
