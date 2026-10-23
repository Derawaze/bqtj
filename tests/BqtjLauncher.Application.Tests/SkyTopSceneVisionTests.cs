using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

/// <summary>合成场景与匿名图标夹具验证通关、几何及物品分类，不包含用户账号数据。</summary>
public sealed class SkyTopSceneVisionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ShortVisibleHealthAtTheTopRightStillLocatesThePlayer(int frame)
    {
        // 真实暂停现场的匿名HUD：只有23px连续绿色，旧24px下限使补捡校准始终等不到人物。
        using var strip = new Bitmap(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "sky-top-short-health.png"));
        var image = Image();
        for (var y = 0; y < 24; y++) for (var x = 0; x < 80; x++)
        {
            var color = strip.GetPixel(frame * 80 + x, y);
            Fill(image, 870 + x, y, 1, 1, color.R, color.G, color.B);
        }
        Assert.Equal(new BqtjLauncher.Application.ScriptRect(910, 48, 12, 20), SkyTopSceneVision.FindPlayer(image));
        Assert.Equal(SkyTopSceneVision.FindPlayer(image), SkyTopSceneVision.FindTopEdgePlayer(image));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CodexAndGuardGemBesideBloodStonesCannotDisappearFromThePickupList(int frame)
    {
        // 新现场的图鉴和绿色宝石只露出部分本体，血石、枪械及紫色光晕不能进入识别遮罩。
        using var strip = new Bitmap(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "sky-top-overlapped-materials.png"));
        var image = Image();
        for (var y = 0; y < 120; y++) for (var x = 0; x < 190; x++)
        {
            var color = strip.GetPixel(frame * 190 + x, y);
            Fill(image, 530 + x, 90 + y, 1, 1, color.R, color.G, color.B);
        }
        var drops = SkyTopDropVision.Find(image, new(53, 273, 12, 20), new(709, 375, 41, 22));
        foreach (var kind in new[] { SkyTopDropVision.DropKind.GuardGem, SkyTopDropVision.DropKind.HadesCodex })
            Assert.Contains(drops, item => item.Kind == kind && item.Required);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AGuardGemPileAroundBloodStonesCannotBeReportedAsAnEmptyScene(int frame)
    {
        // 真实漏捡现场：多颗守护宝石被血石分割，旧单体/小雨模板无法覆盖这一堆叠外观。
        using var strip = new Bitmap(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "sky-top-guard-pile.png"));
        var image = Image();
        for (var y = 0; y < 90; y++) for (var x = 0; x < 155; x++)
        {
            var color = strip.GetPixel(frame % 2 * 155 + x, frame / 2 * 90 + y);
            Fill(image, 635 + x, 120 + y, 1, 1, color.R, color.G, color.B);
        }
        Assert.Contains(SkyTopDropVision.Find(image, new(33, 243, 12, 20), new(709, 392, 40, 24)),
            item => item.Kind == SkyTopDropVision.DropKind.GuardGem && item.Required
                && item.Bounds.X is >= 700 and < 780 && item.Bounds.Y is >= 130 and < 200);
    }

    [Fact]
    public void TheConfirmedColdGemPileRemainsATargetWhenOtherItemsOverlapIt()
    {
        // 独立现场中已确认的长条冷冻宝石堆，不能为了消除蓝色装置误报而把真实材料一起过滤。
        var drops = SkyTopDropVision.Find(ReadFixture("sky-top-cold-stack.png"), null, null);
        Assert.Contains(drops, item => item.Kind == SkyTopDropVision.DropKind.ColdGem && item.Required && item.Bounds.X is >= 580 and < 640);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABlueDeviceAndTheExitLightCannotBecomeColdGems(bool detectExit)
    {
        // 保留现场的蓝色圆形装置和出口白光；旧晶面规则把两处青色碎边都当作冷冻宝石。
        var image = ReadFixture("sky-top-cold-confusers.png");
        var exit = detectExit ? SkyTopSceneVision.FindExitArrow(image) : null;
        Assert.DoesNotContain(SkyTopDropVision.Find(image, null, exit), item => item.Kind == SkyTopDropVision.DropKind.ColdGem);
    }
    [Theory]
    [InlineData("hades-codex.png", "HadesCodex")]
    [InlineData("cold-gem.png", "ColdGem")]
    [InlineData("guard-gem.png", "GuardGem")]
    public void AnUncollectedMaterialNearThePlayerRemainsVisibleToTheCompletionCheck(string file, string kind)
    {
        // 本体距人物超过实际拾取半径，却在旧55px排除区内；不能因此报告拾取完成。
        var image = Image(); Fill(image, 0, 0, 950, 600, 15, 23, 90);
        using var icon = new Bitmap(System.IO.Path.Combine(SkyTopDropVision.ResourceDirectory, file));
        for (var y = 0; y < icon.Height; y++) for (var x = 0; x < icon.Width; x++)
        {
            var color = icon.GetPixel(x, y);
            Fill(image, 650 + x, 240 + y, 1, 1, color.R, color.G, color.B);
        }
        Assert.Contains(SkyTopDropVision.Find(image, new(624, 204, 12, 20), null), item => item.Kind.ToString() == kind && item.Required);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void LightRainDoesNotHideTheVisibleMaterialPile(int frame)
    {
        // 同一静止掉落堆的真实小雨采样，只保留匿名图标；雨线和物品浮动不能使材料整堆漏读。
        using var strip = new Bitmap(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "sky-top-light-rain-loot.png"));
        var image = Image();
        for (var y = 0; y < 110; y++) for (var x = 0; x < 200; x++)
        {
            var color = strip.GetPixel(frame % 3 * 200 + x, frame / 3 * 110 + y);
            Fill(image, 620 + x, 125 + y, 1, 1, color.R, color.G, color.B);
        }
        var drops = SkyTopDropVision.Find(image, null, null);
        foreach (var name in new[] { "冥王图鉴", "守护宝石" })
            Assert.Contains(drops, item => item.Name == name && item.Required
                && item.Bounds.X is >= 700 and < 800 && item.Bounds.Y is >= 140 and < 210);
    }

    [Fact]
    public void RainCloudsAndTheWhiteExitGlowCannotBecomeMaterials()
    {
        // 同批小雨画面的空白天空、云层、雨线及出口白光，不包含地面材料。
        var image = ReadFixture("sky-top-light-rain-background.png");
        Assert.Empty(SkyTopSceneVision.FindLootGroups(image, null, SkyTopSceneVision.FindExitArrow(image)));
    }

    [Fact]
    public void PlayerHealthAtTheTopLeftCannotImpersonateTheBattleAiSwitch()
    {
        // AI已关闭，但角色在左上角留下三像素高的等级/血条；它不能冒充绿色AI字样。
        Assert.False(SkyTopSceneVision.ReadBattleAi(ReadFixture("sky-top-ai-off-level.png")));
        Assert.True(SkyTopSceneVision.ReadBattleAi(ReadFixture("sky-top-ai-on-level.png")));
    }
    [Fact]
    public void ARewardIconCrossingTheBottomOfTheOverlayCannotSeedPickup()
    {
        var image = Image(); Fill(image, 0, 0, 950, 600, 15, 23, 90);
        using var icon = new Bitmap(System.IO.Path.Combine(SkyTopDropVision.ResourceDirectory, "hades-codex.png"));
        for (var y = 0; y < icon.Height; y++) for (var x = 0; x < icon.Width; x++)
        {
            var color = icon.GetPixel(x, y);
            Fill(image, 350 + x, 60 + y, 1, 1, color.R, color.G, color.B);
        }
        Assert.DoesNotContain(SkyTopDropVision.Find(image, null, null), item => item.Required);
    }
    [Fact]
    public void AiOffRequiresARealHudRatherThanABlankLoadingFrame()
    {
        var image = Image();
        Assert.Null(SkyTopSceneVision.ReadBattleAi(image));
        Fill(image, 60, 10, 130, 10, 255, 160, 0);
        Assert.False(SkyTopSceneVision.ReadBattleAi(image));
        Fill(image, 8, 8, 6, 10, 0, 255, 0);
        Assert.True(SkyTopSceneVision.ReadBattleAi(image));
    }
    [Fact]
    public void RoundGreenGlowDoesNotHideTheOnlyRealHealthBar()
    {
        var image = Image(); Fill(image, 9, 76, 38, 3, 20, 240, 20);
        // 真实现场的弹药/技能绿光比生命条厚，动画某帧恰好有24px以上的横向扫描线。
        Fill(image, 80, 85, 32, 24, 20, 240, 20);
        var player = SkyTopSceneVision.FindPlayer(image);
        Assert.NotNull(player); Assert.Equal(28, player.Value.X + player.Value.Width / 2);
    }

    [Fact]
    public void SupplyIconsAreRecognizedWithoutEnteringThePickupRoute()
    {
        var drops = SkyTopDropVision.Find(ReadFixture("sky-top-supplies.png"), null, null);
        Assert.Equal(["弹药箱", "弹药箱", "生命药瓶"], drops.Select(item => item.Name).Order());
        Assert.All(drops, item => Assert.False(item.ShouldPickup));
        Assert.Empty(SkyTopSceneVision.FindLootGroups(ReadFixture("sky-top-supplies.png"), null, null));
    }

    [Fact]
    public void RewardBannerChestIsNotAFieldDropEvenWhenItsIconMatches()
    {
        var image = Image(); Fill(image, 0, 0, 950, 600, 15, 23, 90);
        var source = ReadFixture("sky-top-new-materials.png");
        // 将真实稀有宝箱小图标放在固定抽奖栏内，它属于已发放奖励，不是地面掉落。
        for (var row = 0; row < 35; row++)
            Array.Copy(source.Bgr, ((330 + row) * 950 + 700) * 3, image.Bgr, ((20 + row) * 950 + 350) * 3, 43 * 3);
        Assert.Empty(SkyTopDropVision.Find(image, null, null));
    }

    [Fact]
    public void OffhandsAndDevicesAreRecognizedButNeverBecomePickupTargets()
    {
        string[] expected = ["月刺", "屠夫", "沃龙牌棍棒", "地震发生器", "无尽轰炮", "闪电塔", "全域电击器", "能量外壳"];
        var drops = SkyTopDropVision.Find(ReadFixture("sky-top-ignored-equipment.png"), null, null);
        Assert.Equal(expected.Order(), drops.Select(item => item.Name).Order());
        // 用户纠正：第一格的宽刀是屠夫，第二格的细长刃是月刺，不能只核对名称集合。
        Assert.Equal("屠夫", Assert.Single(drops, item => item.Bounds.X is >= 90 and < 160 && item.Bounds.Y < 200).Name);
        Assert.Equal("月刺", Assert.Single(drops, item => item.Bounds.X is >= 290 and < 360 && item.Bounds.Y < 200).Name);
        Assert.All(drops, item => Assert.False(item.ShouldPickup));
    }

    [Fact]
    public void BloodStoneRemainsRecognizableButDoesNotBlockPickupCompletion()
    {
        var expected = new Dictionary<string, bool>
        {
            ["超能石"] = false,
            ["神能石"] = false,
            ["转化石"] = false,
            ["血石"] = true,
            ["生命催化剂"] = false,
            ["远古宝箱"] = true,
            ["传奇宝箱"] = true,
            ["稀有宝箱"] = true
        };
        var drops = SkyTopDropVision.Find(ReadFixture("sky-top-new-materials.png"), null, null);
        Assert.True(expected.Count == drops.Count, string.Join(", ", drops.Select(item => item.Name + " " + item.Bounds)));
        foreach (var item in expected)
            Assert.Equal(item.Value, Assert.Single(drops, drop => drop.Name == item.Key).ShouldPickup);
        Assert.False(Assert.Single(drops, drop => drop.Name == "血石").Required);
    }

    [Fact]
    public void OverlappingDropsKeepThreeSeparateGunsWithoutLearningTheirNeighbours()
    {
        var drops = SkyTopDropVision.Find(ReadFixture("sky-top-overlapping-drops.png"), null, null);
        Assert.Equal(3, drops.Count(item => item.Kind == SkyTopDropVision.DropKind.Weapon));
        Assert.All(drops.Where(item => item.Kind == SkyTopDropVision.DropKind.Weapon), item => Assert.False(item.ShouldPickup));
    }

    [Fact]
    public void ActualLootClassifiesEquipmentButOnlyPicksMaterials()
    {
        // 仅保留战场小图标拼图，没有账号、角色名或完整用户画面；最后一格是误识别过的平滑绿光。
        var image = ReadFixture("sky-top-loot-sprites.png");
        var drops = SkyTopDropVision.Find(image, null, null);
        Assert.Contains(drops, item => item.Kind == SkyTopDropVision.DropKind.Equipment && !item.ShouldPickup && item.Bounds.X is >= 335 and <= 380);
        Assert.Equal(2, drops.Count(item => item.Kind == SkyTopDropVision.DropKind.Equipment));
        Assert.Equal(7, drops.Count(item => item.Kind == SkyTopDropVision.DropKind.Weapon));
        Assert.Contains(drops, item => item.Kind == SkyTopDropVision.DropKind.Voucher && !item.ShouldPickup && item.Bounds.X >= 495);
        Assert.Contains(drops, item => item.Kind == SkyTopDropVision.DropKind.ColdGem && item.ShouldPickup && item.Bounds.X is >= 575 and <= 630);
        Assert.Contains(drops, item => item.Kind == SkyTopDropVision.DropKind.HadesCodex && item.Bounds.X >= 815);
        Assert.Contains(drops, item => item.Kind == SkyTopDropVision.DropKind.GuardGem && item.Bounds.Y >= 295);
        Assert.DoesNotContain(drops, item => item.Bounds.Y < 290 && (item.Bounds.X < 335 || item.Bounds.X is >= 695 and < 765));
        var groups = SkyTopSceneVision.FindLootGroups(image, null, null);
        Assert.DoesNotContain(groups, group => group.Bounds.X is >= 335 and < 575);
        Assert.DoesNotContain(groups, group => group.Bounds.Y >= 400);
    }

    [Fact]
    public void ReadsCurrentWaveAndClockOnlyFromBattleHud()
    {
        var result = SkyTopSceneVision.ReadRound([new("7/7", new(830, 12, 25, 15), .99), new("01:35", new(865, 12, 50, 15), .99)]);
        Assert.NotNull(result); Assert.Equal(7, result.Finished); Assert.Equal(95, result.Seconds);
        Assert.Null(SkyTopSceneVision.ReadRound([new("7/7 01:35", new(300, 300, 100, 15), .99)]));
        Assert.Equal(new SkyTopSceneVision.Round(7, 7, 332), SkyTopSceneVision.ReadRound([new("7/705:32", new(830, 12, 70, 15), .99)]));
    }

    [Fact]
    public void QuotaRequiresGreenRatherThanCyanText()
    {
        var image = Image(); var row = new BqtjLauncher.Application.ScriptRect(100, 200, 80, 15);
        Fill(image, 100, 200, 70, 2, 20, 240, 240);
        Assert.False(SkyTopSceneVision.IsQuotaGreen(image, row));
        Fill(image, 100, 200, 70, 2, 20, 240, 20);
        Assert.True(SkyTopSceneVision.IsQuotaGreen(image, row));
    }

    [Fact]
    public void ASeparatedCodexCounterUsesItsOwnBaselineAndGreenPixels()
    {
        // 现场达到上限后，白色名称与绿色数字会被OCR拆成两个框；允许实际掉落略超上限。
        var quota = SkyTopSceneVision.ReadQuota(ReadFixture("sky-top-quota-split.png"),
        [
            new("冷冻宝石（208/205）", new(193, 522, 115, 17), .97),
            new("守护宝石", new(192, 539, 53, 17), .99),
            new("冥王图鉴", new(192, 554, 56, 20), .99),
            new("{29/27)", new(241, 557, 55, 14), .87)
        ]);
        Assert.NotNull(quota); Assert.True(quota.IsFull);
        Assert.Equal(208, quota.Gems.Collected); Assert.Equal(29, quota.Codex.Collected);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(0, false)]
    [InlineData(20, true)]
    public void SplitCountersCannotBorrowAnotherRowOrCyanCompletion(int offsetY, bool green)
    {
        var image = Image();
        Fill(image, 235, 502, 50, 12, 20, 240, 20);
        Fill(image, 235, 537 + offsetY, 50, 12, 20, 240, green ? (byte)20 : (byte)240);
        var quota = SkyTopSceneVision.ReadQuota(image,
        [
            new("冷冻宝石", new(150, 500, 80, 18), .99), new("(10/10)", new(235, 502, 55, 14), .99),
            new("冥王图鉴", new(150, 535, 80, 18), .99), new("(5/5)", new(235, 537 + offsetY, 55, 14), .99)
        ]);
        if (offsetY != 0) Assert.Null(quota);
        else { Assert.NotNull(quota); Assert.Equal(green, quota.IsFull); }
    }

    [Fact]
    public void PlayerRequiresOneSceneHealthBarAndIgnoresTopHud()
    {
        var image = Image(); Fill(image, 45, 15, 140, 3, 20, 240, 20);
        Assert.Null(SkyTopSceneVision.FindPlayer(image));
        Fill(image, 300, 250, 40, 3, 20, 240, 20);
        var body = SkyTopSceneVision.FindPlayer(image);
        Assert.NotNull(body); Assert.Equal(320, body.Value.X + body.Value.Width / 2);
        Fill(image, 500, 330, 45, 3, 20, 240, 20);
        Assert.Null(SkyTopSceneVision.FindPlayer(image));
    }

    [Fact]
    public void PlayerAtTopEdgeStillUsesItsSceneBarRatherThanHud()
    {
        var image = Image(); Fill(image, 45, 15, 140, 3, 20, 240, 20);
        Fill(image, 52, 5, 50, 2, 20, 240, 20);
        var player = SkyTopSceneVision.FindPlayer(image);
        Assert.NotNull(player); Assert.Equal(77, player.Value.X + player.Value.Width / 2);
    }

    [Fact]
    public void BrightBlocksAndQualityGlowsWithoutAnItemPatternDoNotBecomeTargets()
    {
        var image = Image(); Fill(image, 300, 10, 30, 30, 250, 40, 240);
        Fill(image, 100, 200, 80, 60, 175, 220, 255);
        Fill(image, 450, 200, 15, 15, 250, 40, 240);
        Fill(image, 474, 202, 12, 12, 20, 240, 250);
        Fill(image, 350, 73, 10, 10, 250, 180, 20);
        Fill(image, 600, 320, 20, 30, 20, 250, 20);
        Fill(image, 700, 300, 50, 30, 250, 180, 20);
        var groups = SkyTopSceneVision.FindLootGroups(image, new(600, 300, 12, 20), new(700, 300, 50, 30));
        Assert.Empty(groups);
    }

    [Fact]
    public void FixedRewardBarDoesNotCountAsABlockingGamePanel()
    {
        var image = Image(); Fill(image, 0, 0, 950, 600, 15, 23, 90);
        Fill(image, 260, 0, 425, 73, 10, 10, 10);
        Assert.False(SkyTopSceneVision.HasBlockingPanel(image));
        Fill(image, 150, 100, 650, 360, 20, 20, 20);
        Assert.True(SkyTopSceneVision.HasBlockingPanel(image));
    }

    [Fact]
    public void GreenSupplyOrGlowCannotBecomeGuardGemOnlyBecauseItHasAnOutline()
    {
        var image = Image(); Fill(image, 0, 0, 950, 600, 15, 23, 90);
        Fill(image, 400, 250, 25, 25, 40, 250, 40);
        Assert.Empty(SkyTopSceneVision.FindLootGroups(image, null, null));
        // 仅有描边和绿色也不足以认定是守护宝石，弹药箱不能成为追逐目标。
        Fill(image, 500, 250, 24, 25, 10, 10, 10);
        Fill(image, 502, 252, 20, 21, 40, 250, 40);
        Assert.Empty(SkyTopSceneVision.FindLootGroups(image, null, null));
    }

    [Fact]
    public void ExitArrowMustHaveAThinTipAndLargeConnectedBody()
    {
        var image = Image(); Fill(image, 650, 300, 50, 30, 230, 160, 10);
        Assert.Null(SkyTopSceneVision.FindExitArrow(image));
        image = Image(); Fill(image, 650, 310, 25, 16, 230, 160, 10);
        for (var offset = 0; offset < 25; offset++) Fill(image, 674 + offset, 298 + offset / 2, 1, 40 - offset * 3 / 2, 230, 160, 10);
        Assert.NotNull(SkyTopSceneVision.FindExitArrow(image));
    }

    [Theory]
    [InlineData(45)]
    [InlineData(90)]
    [InlineData(135)]
    [InlineData(180)]
    [InlineData(225)]
    [InlineData(270)]
    public void ExitArrowCanRotateWhileTheBattleRemainsCleared(int degrees)
    {
        var source = Image(); Fill(source, 650, 310, 25, 16, 230, 160, 10);
        for (var offset = 0; offset < 25; offset++) Fill(source, 674 + offset, 298 + offset / 2, 1, 40 - offset * 3 / 2, 230, 160, 10);
        var rotated = Image(); var radians = degrees * Math.PI / 180;
        for (var y = 295; y < 340; y++) for (var x = 648; x < 702; x++)
        {
            if (source.Bgr[(y * 950 + x) * 3 + 2] == 0) continue;
            var dx = x - 674; var dy = y - 318;
            var targetX = 674 + (int)Math.Round(dx * Math.Cos(radians) - dy * Math.Sin(radians));
            var targetY = 318 + (int)Math.Round(dx * Math.Sin(radians) + dy * Math.Cos(radians));
            Fill(rotated, targetX, targetY, 2, 2, 230, 160, 10);
        }
        Assert.NotNull(SkyTopSceneVision.FindExitArrow(rotated));
    }

    // 夹具仅有匿名HUD或图标，不含账号、角色名或完整背包；小图保持原坐标放入标准画面。
    private static ScriptPixels ReadFixture(string file)
    {
        using var bitmap = new System.Drawing.Bitmap(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
        var data = bitmap.LockBits(new(0, 0, bitmap.Width, bitmap.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        var image = Image();
        try
        {
            for (var row = 0; row < bitmap.Height; row++) System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + row * data.Stride, image.Bgr, row * 950 * 3, bitmap.Width * 3);
        }
        finally { bitmap.UnlockBits(data); }
        return image;
    }

    private static ScriptPixels Image() => new(950, 600, new byte[950 * 600 * 3]);
    private static void Fill(ScriptPixels image, int x, int y, int width, int height, byte red, byte green, byte blue)
    {
        for (var py = y; py < y + height; py++) for (var px = x; px < x + width; px++)
        {
            var offset = (py * image.Width + px) * 3;
            image.Bgr[offset] = blue; image.Bgr[offset + 1] = green; image.Bgr[offset + 2] = red;
        }
    }
}
