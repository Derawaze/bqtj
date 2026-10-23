using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using BqtjLauncher.Domain;
using BqtjLauncher.Infrastructure;
using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

/// <summary>官方流程使用合成菜单/像素，验证实际执行器选档、保存观察与确认退出，不读取用户游戏。</summary>
public sealed class OfficialScriptExecutorTests
{
    private static readonly System.Text.Json.JsonSerializerOptions TraceJsonOptions = new()
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly string[] ExpectedActions = ["读取存档", "存档8", "助理", "日常操作复制", "运行", "确定运行", "退出存档", "确定退出"];
    private static readonly string[] TakeoverActions = ["系统", "退出存档", "确定退出"];
    private static readonly string[] OneRoundActions = ["寒光市", "天空顶", "正常", "地狱四", "关卡系统", "退出关卡", "确定退出关卡", "关闭评价"];
    private static readonly string[] RetryRoundActions = ["关卡系统", "退出关卡", "确定退出关卡", "关闭评价", "寒光市", "天空顶", "正常", "地狱四"];
    private static readonly bool[] CompletedAiChanges = [false, true];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresidentWaitsForAutomaticPickupWithoutMovementOrDismantling(bool fillQuota)
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-president-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构总统刷取")).Id))!;
            var vision = new MenuVision
            {
                State = "world",
                FarmMode = "正常",
                DropWaveDigitAfterClear = true,
                PlayerAtSurveyPoint = true,
                ReplayHud = ["7/7 01:35"],
                QuotaFull = true,
                RoundTenHasMaterial = true,
                BagFull = true
            };
            var trace = new List<ScriptTraceEvent>();
            var elapsed = Stopwatch.StartNew();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await new OfficialScriptExecutor(vision, trace: trace.Add).FarmDailyMaterialsAsync(session,
                new("天空顶", "地狱四", fillQuota ? null : 2, true), (_, _) => { }, stop.Token);
            var rounds = fillQuota ? 10 : 2;
            Assert.InRange(elapsed.Elapsed.TotalSeconds, rounds, 25);
            Assert.Equal(rounds - 1, vision.Replays); Assert.Equal("world", vision.State);
            Assert.Equal(fillQuota ? 1 : 0, vision.QuotaReads);
            Assert.Equal(0, vision.Movements); Assert.Empty(vision.InventoryActions);
            Assert.DoesNotContain(false, vision.AiChanges);
            Assert.Equal(rounds, trace.Count(item => item.Event == "president-auto-pickup"));
            Assert.DoesNotContain(trace, item => item.Event is "initial-drop-check" or "fixed-sweep-start" or "inventory-full");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task BlueQuotaDigitsAreReadSeparatelyFromTheSameSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-blue-quota-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构蓝色计数")).Id))!;
            var vision = new MenuVision
            {
                State = "world",
                FarmMode = "正常",
                DropWaveDigitAfterClear = true,
                PlayerAtSurveyPoint = true,
                ReplayHud = ["7/7 01:35"],
                SplitQuotaDigits = true
            };
            var events = new List<ScriptTraceEvent>();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(18));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OfficialScriptExecutor(vision, trace: item =>
            {
                events.Add(item);
                if (item.Event == "quota-read" && item.Result == "not-full") stop.Cancel();
            }).FarmDailyMaterialsAsync(session, new("天空顶", "地狱四", null), (_, _) => { }, stop.Token));
            Assert.Contains(events, item => item.Event == "quota-read" && item.Result == "not-full");
            Assert.Single(vision.CounterSnapshots); Assert.True(vision.CounterSnapshots[0] > 0);
            Assert.Equal(vision.LastQuotaSnapshot, vision.CounterSnapshots[0]);
            Assert.Equal(9, vision.Replays);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ReplayUsesFixedButtonsWhenTheMenuLabelIsHidden()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-fixed-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构固定重玩")).Id))!;
            var vision = new MenuVision
            {
                State = "world",
                FarmMode = "正常",
                DropWaveDigitAfterClear = true,
                PlayerAtSurveyPoint = true,
                ReplayHud = ["7/7 01:35"],
                HideReplayLabel = true
            };
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await new OfficialScriptExecutor(vision).FarmDailyMaterialsAsync(session, new("天空顶", "地狱四", 2), (_, _) => { }, stop.Token);
            Assert.Equal(1, vision.Replays); Assert.Equal("world", vision.State);
            Assert.Equal((470, 309), vision.ReplayPoint); Assert.Equal((420, 315), vision.ReplayConfirmPoint);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InventoryDialogsUseTheirFixedButtonsWithoutReadingTheirLabels(bool labelsHidden)
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-fixed-inventory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构固定拆解")).Id))!;
            var vision = new MenuVision
            {
                State = "world",
                FarmMode = "正常",
                DropWaveDigitAfterClear = true,
                PlayerAtSurveyPoint = true,
                BagFull = true,
                ActualInventoryResult = true,
                HideInventoryDialogText = labelsHidden
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await new OfficialScriptExecutor(vision).FarmDailyMaterialsAsync(session, new("天空顶", "地狱四", 1), (_, _) => { }, timeout.Token);
            Assert.Equal("world", vision.State); Assert.Equal(0, vision.InventoryDialogOcrReads);
            Assert.Equal(new (int X, int Y)[] { (420, 315), (475, 315), (420, 315), (475, 315) }, vision.InventoryDialogClicks);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AFullBagIsTidiedBeforePickupAndOnlyFullGenesAreDecomposed(bool genesFull, bool nothingToDismantle)
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-inventory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构满包整理")).Id))!;
            var vision = new MenuVision
            {
                State = "world",
                FarmMode = "正常",
                DropWaveDigitAfterClear = true,
                PlayerAtSurveyPoint = true,
                BagFull = true,
                GenesFull = genesFull,
                NothingToDismantle = nothingToDismantle,
                DelayedInventoryResult = true
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await new OfficialScriptExecutor(vision).FarmDailyMaterialsAsync(session, new("天空顶", "地狱四", 1), (_, _) => { }, timeout.Token);
            var expected = new List<string> { "打开背包", "武器", "一键拆解武器", "确认拆解武器", "确认武器结果",
                "装备", "一键拆解装备", "确认拆解装备", "确认装备结果" };
            if (genesFull) expected.AddRange(["基因体", "一键分解", "确认分解"]);
            expected.Add("关闭背包");
            Assert.Equal(expected, vision.InventoryActions);
            Assert.True(vision.TidiedBeforeMovement); Assert.Equal("world", vision.State);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InventoryTooltipsAreClearedBeforeReadingOrClickingTheControls(bool genesFull)
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-inventory-tooltip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构悬停整理")).Id))!;
            var vision = new MenuVision
            {
                State = "world",
                FarmMode = "正常",
                DropWaveDigitAfterClear = true,
                PlayerAtSurveyPoint = true,
                BagFull = true,
                GenesFull = genesFull,
                InventoryTooltips = true
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await new OfficialScriptExecutor(vision).FarmDailyMaterialsAsync(session, new("天空顶", "地狱四", 1), (_, _) => { }, timeout.Token);
            Assert.Equal("world", vision.State); Assert.True(vision.BagTidied); Assert.True(vision.TidiedBeforeMovement);
            Assert.Equal(2, vision.InventoryActions.Count(item => item.StartsWith("一键拆解", StringComparison.Ordinal)));
            Assert.Equal(genesFull ? 1 : 0, vision.InventoryActions.Count(item => item == "一键分解"));
            Assert.NotEmpty(vision.InventoryHoverPoints);
            Assert.All(vision.InventoryHoverPoints, point => Assert.Equal((720, 32), point));
            Assert.Equal(0, vision.QuotaReads);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AnUnknownDismantleResultDoesNotConfirmAgainOrReplay()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-inventory-result-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构拆解异常")).Id))!;
            var vision = new MenuVision { State = "world", FarmMode = "正常", BagFull = true, UnknownDismantleResult = true };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OfficialScriptExecutor(vision)
                .FarmDailyMaterialsAsync(session, new("天空顶", "地狱四", 1), (_, _) => { }, timeout.Token));
            Assert.Equal("inventory-weapon-result", vision.State); Assert.Equal(0, vision.Replays);
            Assert.Equal(1, vision.InventoryActions.Count(value => value == "确认拆解武器"));
            Assert.DoesNotContain("确认武器结果", vision.InventoryActions);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuotaReadingRefreshesTheBackgroundHoverWhenTheFirstMessagesDoNotOpenIt(bool hideLabel)
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-quota-hover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构后台悬停")).Id))!;
            var vision = new MenuVision
            {
                State = "world",
                FarmMode = "正常",
                DropWaveDigitAfterClear = true,
                PlayerAtSurveyPoint = true,
                QuotaFull = true,
                ReplayHud = ["7/7 01:35"],
                HoverAppearsAfter = 3,
                HideQuotaLabel = hideLabel
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(16));
            await new OfficialScriptExecutor(vision).FarmDailyMaterialsAsync(session, new("天空顶", "地狱四", null), (_, _) => { }, timeout.Token);
            Assert.Equal(3, vision.QuotaReads); Assert.Equal(9, vision.Replays); Assert.Equal("world", vision.State);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ThreeUnreadableEmptyRoundsDoNotExitOrClaimBothCountersAreFull()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-quota-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构限额保底")).Id))!;
            var vision = new MenuVision
            {
                State = "world",
                FarmMode = "正常",
                DropWaveDigitAfterClear = true,
                PlayerAtSurveyPoint = true,
                ReplayHud = ["7/7 01:35"],
                HoverAppearsAfter = int.MaxValue
            };
            var events = new List<ScriptTraceEvent>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OfficialScriptExecutor(vision, trace: item =>
            {
                events.Add(item);
                if (events.Count(entry => entry.Event == "quota-unconfirmed-empty") == 3) timeout.Cancel();
            }).FarmDailyMaterialsAsync(session, new("天空顶", "地狱四", null), (_, _) => { }, timeout.Token));
            Assert.Equal(11, vision.Replays); Assert.Equal(12, vision.QuotaReads); Assert.Equal("battle", vision.State);
            Assert.Equal(3, events.Count(item => item.Event == "quota-unconfirmed-empty"));
            Assert.DoesNotContain(events, item => item.Event == "farming-fallback");
            Assert.DoesNotContain(events, item => item.Event == "quota-read" && item.Result == "full");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("简单", 370, 182)]
    [InlineData("超难", 584, 237)]
    [InlineData("炼狱", 584, 292)]
    [InlineData("地狱三", 370, 402)]
    [InlineData("地狱四", 584, 402)]
    public async Task DifficultySelectionUsesTheFixedButtonWhenItsTextIsCovered(string difficulty, int x, int y)
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-difficulty-tooltip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构难度遮挡")).Id))!;
            var vision = new MenuVision
            {
                State = "world",
                FarmMode = "正常",
                DropWaveDigitAfterClear = true,
                DifficultyTooltipCoversLabels = true
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await new OfficialScriptExecutor(vision).FarmDailyMaterialsAsync(session,
                new("天空顶", difficulty, 1), (_, _) => { }, timeout.Token);
            Assert.Equal<(int X, int Y)?>((x, y), vision.DifficultyClick);
            Assert.Equal(0, vision.DifficultyLabelReads);
            Assert.Equal("world", vision.State);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task EarlyPickupHandlesAnEdgeAFlickeringReadAndNewlyExposedMaterial(bool leftEdge, bool flicker, bool second)
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-pickup-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构拾取复查")).Id))!;
            var vision = new MenuVision
            {
                State = "world",
                FarmMode = "正常",
                DropWaveDigitAfterClear = true,
                VisibleMaterialDuringRise = true,
                StartAtLeftEdge = leftEdge,
                FlickerFirstPickupReview = flicker,
                ExposeSecondMaterial = second
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
            await new OfficialScriptExecutor(vision).FarmDailyMaterialsAsync(session,
                new("天空顶", "地狱四", 1), (_, _) => { }, timeout.Token);
            Assert.True(vision.EarlyMaterialCollected, "识别暂时消失不能代替实际走到材料附近。");
            if (second) Assert.True(vision.SecondMaterialCollected, "补捡后新显露的同堆材料仍需处理，不能立即暂停。");
            Assert.Equal("world", vision.State);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AHiddenPlayerContinuesTheUpperSearchInsteadOfBlindlyMovingDown()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-covered-pickup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构遮挡拾取")).Id))!;
            var vision = new MenuVision
            {
                State = "world",
                FarmMode = "正常",
                DropWaveDigitAfterClear = true,
                VisibleMaterialDuringRise = true,
                HideHealthUntilRight = true,
                ShowYellowMapDot = true
            };
            var stages = new List<string>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
            await new OfficialScriptExecutor(vision).FarmDailyMaterialsAsync(session,
                new("天空顶", "地狱四", 1), (stage, _) => stages.Add(stage), timeout.Token);
            Assert.True(vision.EarlyMaterialCollected);
            Assert.DoesNotContain(stages, stage => stage.Contains("显露人物", StringComparison.Ordinal));
            var right = vision.MovementKeys.IndexOf('D');
            Assert.True(right > 0); Assert.All(vision.MovementKeys.Take(right), key => Assert.Equal('W', key));
            Assert.Equal("world", vision.State);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AVisibleMaterialInterruptsTheFixedRouteAndIsCollectedImmediately()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-early-pickup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构提前拾取")).Id))!;
            var vision = new MenuVision
            {
                State = "world",
                FarmMode = "正常",
                DropWaveDigitAfterClear = true,
                VisibleMaterialDuringRise = true
            };
            var trace = new List<ScriptTraceEvent>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await new OfficialScriptExecutor(vision, trace: trace.Add).FarmDailyMaterialsAsync(session,
                new("天空顶", "地狱四", 1), (_, _) => { }, timeout.Token);
            Assert.Equal(180, vision.MovementDurations.TakeWhile((_, index) => vision.MovementKeys[index] == 'W').Sum());
            Assert.Contains(trace, item => item.Event == "fixed-route-target" && item.Result == "W");
            Assert.True(vision.EarlyMaterialCollected);
            Assert.Equal("world", vision.State);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SkyTopEntryUsesItsFixedPositionWithoutReadingTheLevelTitle()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-map-tooltip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构地图悬停")).Id))!;
            var vision = new MenuVision
            {
                State = "world",
                FarmMode = "正常",
                DropWaveDigitAfterClear = true,
                MapTooltipCoversTitle = true
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await new OfficialScriptExecutor(vision).FarmDailyMaterialsAsync(session,
                new("天空顶", "地狱四", 1), (_, _) => { }, timeout.Token);
            Assert.Equal(0, vision.MapTooltipDismissals);
            Assert.Equal(0, vision.MapTitleReads);
            Assert.Equal<(int X, int Y)?>((676, 444), vision.LevelClick);
            Assert.Equal(OneRoundActions, vision.Actions);
            Assert.Equal("world", vision.State);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ALevelBarAfterTheAiToggleDoesNotBlockTheFirstRise()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-ai-level-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构AI血条遮挡")).Id))!;
            var vision = new MenuVision
            {
                State = "world",
                FarmMode = "正常",
                DropWaveDigitAfterClear = true,
                LevelBarAfterAiOff = true,
                ShowYellowMapDot = true
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await new OfficialScriptExecutor(vision).FarmDailyMaterialsAsync(session,
                new("天空顶", "地狱四", 1), (_, _) => { }, timeout.Token);
            Assert.Equal(CompletedAiChanges, vision.AiChanges);
            Assert.Equal(1, vision.CombinedAiRiseCount);
            Assert.Contains('D', vision.MovementKeys);
            Assert.Equal("world", vision.State);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HiddenHealthUsesYellowMapDotOrFinishesAtTheInputLimit(bool showYellow)
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-mini-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构黄点边界")).Id))!;
            var vision = new MenuVision
            {
                State = "world",
                FarmMode = "正常",
                DropWaveDigitAfterClear = true,
                CornerMaterial = true,
                HidePickupHealth = true,
                ShowYellowMapDot = showYellow
            };
            var trace = new List<ScriptTraceEvent>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await new OfficialScriptExecutor(vision, trace: trace.Add).FarmDailyMaterialsAsync(session,
                new("天空顶", "地狱四", 1), (_, _) => { }, timeout.Token);
            var right = vision.MovementKeys.IndexOf('D');
            var rise = vision.MovementDurations.Take(right).Sum();
            Assert.InRange(rise, 1, 1200);
            if (!showYellow) Assert.Equal(1200, rise);
            Assert.Equal(1, vision.CombinedAiRiseCount);
            Assert.Contains(trace, item => item.Event == (showYellow ? "boundary-confirmed" : "boundary-input-limit"));
            Assert.True(vision.CornerMaterialCollected); Assert.Equal("world", vision.State);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PauseAfterCombinedFirstRiseReobservesBeforeContinuing()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-sweep-pause-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构扫取暂停")).Id))!;
            var control = new ScriptRunControl(); var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var vision = new MenuVision
            {
                State = "world",
                FarmMode = "正常",
                DropWaveDigitAfterClear = true,
                OnInitialSweepObservation = () => { control.Pause(); paused.TrySetResult(); }
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var running = new OfficialScriptExecutor(vision).ForRun(control)
                .FarmDailyMaterialsAsync(session, new("天空顶", "地狱四", 1), (_, _) => { }, timeout.Token);
            await paused.Task.WaitAsync(timeout.Token); Assert.Equal(1, vision.Movements);
            control.Resume(); await running;
            Assert.Equal("world", vision.State);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task OnlyAnEmptyFirstScreenFromRoundTenCanOpenTheQuotaPanel(bool materialsVisible, bool quotaFull)
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-quota-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构十轮检查")).Id))!;
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var vision = new MenuVision
            {
                State = "world",
                FarmMode = "正常",
                DropWaveDigitAfterClear = true,
                PlayerAtSurveyPoint = true,
                QuotaFull = quotaFull,
                ReplayHud = ["7/7 01:35"],
                RoundTenHasMaterial = materialsVisible
            };
            if (materialsVisible || !quotaFull) vision.MovementObserved = () => { if (vision.Replays == 9) cancel.Cancel(); };
            var run = new OfficialScriptExecutor(vision).FarmDailyMaterialsAsync(session, new("天空顶", "地狱四", null), (_, _) => { }, cancel.Token);
            if (materialsVisible || !quotaFull) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            else await run;
            Assert.Equal(9, vision.Replays);
            Assert.Equal(materialsVisible ? 0 : 1, vision.QuotaReads);
            Assert.Equal(materialsVisible || !quotaFull ? "battle" : "world", vision.State);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("vision")]
    [InlineData("capture")]
    [InlineData("controller-connect")]
    [InlineData("recognition")]
    public async Task FirstObservationFailureReconnectsOnceThenReadsAllEightSaves(string code)
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-first-observation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构首次识别")).Id))!;
            var vision = new MenuVision { FailureReads = [1], FailureCode = code };
            var trace = new List<ScriptTraceEvent>();
            var saves = await new OfficialScriptExecutor(vision, trace: trace.Add).ReadSavesAsync(session, _ => { }, default);
            Assert.Equal(8, saves.Count);
            Assert.Equal(1, vision.Releases);
            Assert.Equal("读取存档", Assert.Single(vision.Actions));
            Assert.Equal(code, Assert.Single(trace, entry => entry.Event == "initial-observation-retry").Result);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PersistentOrLaterObservationFailureIsNotRepeatedIndefinitely(bool initial)
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-observation-limit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构观察中断")).Id))!;
            var vision = new MenuVision { FailureReads = initial ? [1, 2, 3] : [2] };
            await Assert.ThrowsAsync<InvalidOperationException>(() => new OfficialScriptExecutor(vision).ReadSavesAsync(session, _ => { }, default));
            Assert.Equal(2, vision.ReadCount);
            Assert.Equal(initial ? 1 : 0, vision.Releases);
            Assert.Empty(vision.Actions);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("1/7 00:01")]
    [InlineData("3/7 00:08")]
    [InlineData("7/7 00:06")]
    public async Task ReplayImmediatelyResumesRecognitionWithoutRequiringWaveOrTimerReset(string firstHud)
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-replay-banner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构快速重玩")).Id))!;
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var vision = new MenuVision
            {
                State = "world",
                FarmMode = "正常",
                ShowRewardBanner = true,
                BattleWaveText = "7/7 00:06",
                DropWaveDigitAfterClear = true,
                PlayerAtSurveyPoint = true,
                QuotaFull = true,
                ReplayHud = [firstHud, "7/7 00:06"]
            };
            var completions = 0; var replayHudReadsAtCompletion = 0;
            var stages = new List<string>();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OfficialScriptExecutor(vision)
                .FarmDailyMaterialsAsync(session, new("天空顶", "地狱四", 2), (stage, _) =>
                {
                    stages.Add(stage);
                    if (stage != "通关已确认，整理掉落清单" || ++completions != 2) return;
                    replayHudReadsAtCompletion = vision.ReplayHudReads; cancel.Cancel();
                }, cancel.Token));
            Assert.Equal(1, vision.Replays);
            Assert.Equal(2, completions);
            // 确认重玩后立即读取新截图；即使两轮都在6秒通关，也不等波数或计时复位。
            Assert.Equal(1, replayHudReadsAtCompletion);
            Assert.DoesNotContain(stages, stage => stage.Contains("等待本次重玩开始", StringComparison.Ordinal));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FixedSweepReachesTheTopThenRightAndCollectsTheMaterialOutsideTheOldRectangle()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-search-limit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构搜索上限")).Id))!;
            var vision = new MenuVision { State = "world", FarmMode = "正常", DropWaveDigitAfterClear = true, CornerMaterial = true };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var trace = new List<ScriptTraceEvent>();
            await new OfficialScriptExecutor(vision, trace: trace.Add)
                .FarmDailyMaterialsAsync(session, new("天空顶", "地狱四", 1), (_, _) => { }, timeout.Token);
            Assert.True(vision.CornerMaterialCollected, "旧出口矩形没有覆盖右上方材料；必须先到顶再沿顶向右通过它。");
            var firstRight = vision.MovementKeys.IndexOf('D');
            Assert.True(firstRight > 0);
            Assert.All(vision.MovementKeys.Take(firstRight), key => Assert.Equal('W', key));
            Assert.All(vision.MovementKeys.Skip(firstRight), key => Assert.Equal('D', key));
            Assert.True(vision.MovementDurations.Take(firstRight).Sum() < 1200, "已经到顶时应立即结束上移，不耗完固定时长。");
            Assert.True(vision.MovementDurations.Skip(firstRight).Sum() < 1800, "已经到右边界时应立即结束右移。");
            Assert.Single(trace, entry => entry.Event == "fixed-sweep-review" && entry.TextCount == 0);
            Assert.Equal(0, vision.Replays); Assert.Equal("world", vision.State);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("日常操作,日常操作复制", "", "日常操作复制")]
    [InlineData("日常操作", "", "日常操作")]
    [InlineData("日常操作复制,我的日常", "我的日常", "我的日常")]
    public async Task DailySelectsThePreferredOrSpecifiedNameAndConfirmsItsFinalCheck(string available, string requested, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-fast-daily-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构快速日常")).Id))!;
            var vision = new MenuVision { State = "world", FastDaily = true, DailyNames = available.Split(',') };
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var definition = new ScriptDefinition { Id = "daily", Entry = "daily", Flow = [new() { Id = "daily", Type = "runOfficialDaily", Text = requested }] };
            await new OfficialScriptExecutor(vision).ExecuteAsync(session, definition, new() { SaveNumber = 5 }, SpeedMultiplier.Create(2), (_, _) => { }, cancel.Token);
            Assert.Equal("saved", vision.State);
            Assert.DoesNotContain("保存存档", vision.Actions);
            Assert.Equal(expected, vision.Actions[1]);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MissingInitialMaterialsStartsByAscendingWithoutOpeningTheQuotaMenu()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构搜索账号")).Id))!;
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var vision = new MenuVision { State = "world", FarmMode = "正常", DropWaveDigitAfterClear = true, OnMovement = cancel.Cancel };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OfficialScriptExecutor(vision)
                .FarmDailyMaterialsAsync(session, new("天空顶", "地狱四", 1), (_, _) => { }, cancel.Token));
            Assert.Equal('W', Assert.Single(vision.MovementKeys));
            Assert.Equal(0, vision.QuotaReads);
            Assert.DoesNotContain("关卡系统", vision.Actions);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RetryingFarmingFromExistingBattleReturnsToTownBeforeEnteringAgain()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-farm-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构刷关重试账号")).Id))!;
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var vision = new MenuVision { State = "battle", FarmMode = "正常", OnDifficultySelected = cancel.Cancel };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OfficialScriptExecutor(vision)
                .FarmDailyMaterialsAsync(session, new("天空顶", "地狱四", 1), (_, _) => { }, cancel.Token));
            Assert.Equal(RetryRoundActions, vision.Actions);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DailyClearsItsOwnPanelsBeforeFarmingOpensTheMap(bool firstFrameIsTransition, bool sidebarExpanded)
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-daily-farm-panels-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构日常衔接")).Id))!;
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            // 首帧模拟保存后的过渡空帧；侧栏按实机位置遮住寒光市，不允许直接穿过它点击地图。
            var vision = new MenuVision
            {
                State = "saved",
                SaveOnlyMenu = true,
                HideSystemOnFirstRead = firstFrameIsTransition,
                SidebarExpanded = sidebarExpanded,
                SidebarCollapseDelayFrames = 2,
                FarmMode = "正常98级",
                OnDifficultySelected = cancel.Cancel
            };
            await ((IScriptExecutor)new OfficialScriptExecutor(vision)).DismissDailyPanelsAsync(session, (_, _) => { }, cancel.Token);
            Assert.Equal("world", vision.State);
            Assert.False(vision.SidebarExpanded);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OfficialScriptExecutor(vision)
                .FarmDailyMaterialsAsync(session, new("天空顶", "地狱四", 1), (_, _) => { }, cancel.Token));
            var expected = new List<string> { "关闭系统" };
            if (sidebarExpanded) expected.Add("收起日常侧栏");
            expected.AddRange(["寒光市", "天空顶", "正常98级", "地狱四"]);
            Assert.Equal(expected, vision.Actions);
            Assert.Equal("battle", vision.State);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DailySystemDismissalClosesOnlyItsMenuAndDoesNotExitTheSave(bool saveOnlyMenu)
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-dismiss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构接续账号")).Id))!;
            var vision = new MenuVision { State = "saved", SaveOnlyMenu = saveOnlyMenu };
            var executor = new OfficialScriptExecutor(vision);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await executor.DismissSystemAsync(session, (_, _) => { }, timeout.Token);
            await executor.DismissSystemAsync(session, (_, _) => { }, timeout.Token);
            Assert.Equal("world", vision.State);
            Assert.Equal("关闭系统", Assert.Single(vision.Actions));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PausingBetweenObservationAndClickReobservesBeforeContinuing()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-pause-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构暂停账号")).Id))!;
            var control = new ScriptRunControl();
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var vision = new MenuVision { State = "world", OnFirstRead = () => { control.Pause(); observed.TrySetResult(); } };
            var executor = new OfficialScriptExecutor(vision).ForRun(control);
            var definition = new ScriptDefinition { Id = "pause-check", Entry = "click", Flow = [new() { Id = "click", Type = "click", X = 125, Y = 25 }] };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var running = executor.ExecuteAsync(session, definition, new() { SaveNumber = 1 }, SpeedMultiplier.Create(2), (_, _) => { }, timeout.Token);
            await observed.Task.WaitAsync(timeout.Token);
            await Task.Delay(70, timeout.Token);
            Assert.Empty(vision.Actions);
            control.Resume();
            await running.WaitAsync(timeout.Token);
            Assert.True(vision.ReadCount >= 2);
            Assert.Equal("助理", Assert.Single(vision.Actions));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DirectRewardBannerStartsLootDiscoveryWithoutWaitingForWaveOrExitArrow()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-reward-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new SqliteGameProfileRepository(Path.Combine(root, "fake.db"));
            await using var launcher = new LauncherModule(repository, new FakeRuntime());
            await launcher.InitializeAsync();
            var profile = await launcher.CreateProfileAsync("虚构通关账号");
            var session = (await launcher.StartAutomationSessionAsync(profile.Id))!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var vision = new MenuVision { State = "world", FarmMode = "正常98级", ShowRewardBanner = true, HideAllArrows = true, BattleWaveText = "1/7 00:14" };
            var discovered = false;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OfficialScriptExecutor(vision)
                .FarmDailyMaterialsAsync(session, new("天空顶", "地狱四", 1), (value, _) =>
                { if (value.Contains("整理掉落清单", StringComparison.Ordinal)) { discovered = true; timeout.Cancel(); } }, timeout.Token));
            Assert.True(discovered); Assert.InRange(vision.BattleFrames, 1, 2); Assert.Equal(0, vision.Movements);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("/7 01:35")]
    [InlineData("1/7 01:35")]
    public async Task ConfirmedBattleKeepsCollectingWhenWaveOcrDropsADigit(string waveText)
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-wave-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new SqliteGameProfileRepository(Path.Combine(root, "fake.db"));
            await using var launcher = new LauncherModule(repository, new FakeRuntime());
            await launcher.InitializeAsync();
            var profile = await launcher.CreateProfileAsync("虚构波数账号");
            var session = (await launcher.StartAutomationSessionAsync(profile.Id))!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var vision = new MenuVision { State = "world", FarmMode = "正常98级", DropWaveDigitAfterClear = true, WaveAfterClear = waveText, OnMovement = timeout.Cancel };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OfficialScriptExecutor(vision)
                .FarmDailyMaterialsAsync(session, new("天空顶", "地狱四", 1), (_, _) => { }, timeout.Token));
            Assert.Equal(1, vision.BattleFrames);
            Assert.Equal(1, vision.Movements);
            Assert.Equal('W', Assert.Single(vision.MovementKeys));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OneCompletedRoundExitsTheLevelThroughSettingsAndConfirmation()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-one-round-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构单场账号")).Id))!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var vision = new MenuVision { State = "world", FarmMode = "正常", DropWaveDigitAfterClear = true, PlayerAtSurveyPoint = true, QuotaFull = true };
            await new OfficialScriptExecutor(vision).FarmDailyMaterialsAsync(session, new("天空顶", "地狱四", 1), (_, _) => { }, timeout.Token);
            Assert.Equal("world", vision.State);
            Assert.Equal(OneRoundActions, vision.Actions);
            Assert.True(vision.Movements > 2);
            Assert.Equal(0, vision.QuotaReads);
            Assert.Equal(CompletedAiChanges, vision.AiChanges);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task HiddenHealthBarDoesNotChangeTheUpwardThenRightwardRoute()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-hidden-bar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构遮挡账号")).Id))!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var vision = new MenuVision { State = "world", FarmMode = "正常", ShowRewardBanner = true, OnMovement = timeout.Cancel };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OfficialScriptExecutor(vision)
                .FarmDailyMaterialsAsync(session, new("天空顶", "地狱四", 1), (_, _) => { }, timeout.Token));
            Assert.Equal('W', Assert.Single(vision.MovementKeys));
            Assert.Equal(180, Assert.Single(vision.MovementDurations));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConfirmedBattleCanOpenItsSystemGearWhenLaterWaveTextIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-menu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new SqliteGameProfileRepository(Path.Combine(root, "fake.db"));
            await using var launcher = new LauncherModule(repository, new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构战后账号")).Id))!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
            var vision = new MenuVision { State = "world", FarmMode = "正常", DropWaveDigitAfterClear = true, WaveAfterClear = "/7", PlayerAtSurveyPoint = true, OnBattleSystemOpened = timeout.Cancel };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OfficialScriptExecutor(vision)
                .FarmDailyMaterialsAsync(session, new("天空顶", "地狱四", 1), (_, _) => { }, timeout.Token));
            Assert.Contains("关卡系统", vision.Actions);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConfirmedClearDoesNotWaitForAnAnimatedArrowDuringPickupSearch()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-arrow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new SqliteGameProfileRepository(Path.Combine(root, "fake.db"));
            await using var launcher = new LauncherModule(repository, new FakeRuntime());
            await launcher.InitializeAsync();
            var profile = await launcher.CreateProfileAsync("虚构动画账号");
            var session = (await launcher.StartAutomationSessionAsync(profile.Id))!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var vision = new MenuVision { State = "world", FarmMode = "正常98级", MissingSecondArrow = true, DropWaveDigitAfterClear = true, OnMovement = timeout.Cancel };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OfficialScriptExecutor(vision)
                .FarmDailyMaterialsAsync(session, new("天空顶", "地狱四", 1), (_, _) => { }, timeout.Token));
            Assert.Equal(1, vision.BattleFrames);
            Assert.Equal('W', Assert.Single(vision.MovementKeys));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("正常")]
    [InlineData("正常98级")]
    public async Task FarmingSelectsNormalModeWithDisplayedLevel(string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new SqliteGameProfileRepository(Path.Combine(root, "fake.db"));
            await using var launcher = new LauncherModule(repository, new FakeRuntime());
            await launcher.InitializeAsync();
            var profile = await launcher.CreateProfileAsync("虚构刷关账号");
            var session = (await launcher.StartAutomationSessionAsync(profile.Id))!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            // 使用现场OCR“正常98级”，选到难度后结束，不进入战斗或发送走位。
            var vision = new MenuVision { State = "world", FarmMode = mode, OnDifficultySelected = timeout.Cancel };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OfficialScriptExecutor(vision)
                .FarmDailyMaterialsAsync(session, new("天空顶", "地狱四", 1), (_, _) => { }, timeout.Token));
            Assert.Equal(new[] { "寒光市", "天空顶", mode, "地狱四" }, vision.Actions);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task NormalModeClickWaitsForTheNextPageAndCanRetryAnUnchangedModePanel()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-mode-ack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构模式确认")).Id))!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var vision = new MenuVision
            {
                State = "world",
                FarmMode = "正常98级",
                IgnoreFirstModeClick = true,
                DelayDifficultyFrames = 2,
                OnDifficultySelected = timeout.Cancel
            };
            var trace = new List<ScriptTraceEvent>();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OfficialScriptExecutor(vision, trace: trace.Add)
                .FarmDailyMaterialsAsync(session, new("天空顶", "地狱四", 1), (_, _) => { }, timeout.Token));
            string[] expected = ["寒光市", "天空顶", "正常98级", "正常98级", "地狱四"];
            Assert.Equal(expected, vision.Actions);
            Assert.Equal("battle", vision.State);
            Assert.Equal(new int?[] { 1, 2 }, trace.Where(entry => entry.Event == "normal-mode-attempt").Select(entry => entry.Attempt));
            var confirmation = Assert.Single(trace, entry => entry.Event == "normal-mode-confirmed");
            Assert.Contains(trace, entry => entry.Event == "click-ack" && entry.At < confirmation.At);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("difficulty")]
    public async Task FarmingCanContinueFromItsRetainedModeOrDifficultyPanel(string state)
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-mode-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构模式继续")).Id))!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var vision = new MenuVision { State = state, FarmMode = "正常98级", OnDifficultySelected = timeout.Cancel };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OfficialScriptExecutor(vision)
                .FarmDailyMaterialsAsync(session, new("天空顶", "地狱四", 1), (_, _) => { }, timeout.Token));
            Assert.DoesNotContain("寒光市", vision.Actions);
            Assert.Equal(state == "mode" ? 2 : 1, vision.Actions.Count);
            Assert.Equal("地狱四", vision.Actions[^1]);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ScriptTraceKeepsClickEvidenceWithoutCustomOcrOrStepNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-safe-trace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构私有账号名")).Id))!;
            var trace = new List<ScriptTraceEvent>();
            var vision = new MenuVision { State = "world", ExtraOcr = "虚构口令_不进入日志" };
            var executor = new OfficialScriptExecutor(vision, trace: trace.Add).ForRun(new ScriptRunControl());
            var definition = new ScriptDefinition { Entry = "私有步骤名称", Flow = [new() { Id = "私有步骤名称", Type = "click", X = 125, Y = 25 }] };
            await executor.ExecuteAsync(session, definition, new() { SaveNumber = 5 }, SpeedMultiplier.Create(2), (_, _) => { }, default);
            var sent = Assert.Single(trace, entry => entry.Event == "click-send");
            var acknowledged = Assert.Single(trace, entry => entry.Event == "click-ack");
            Assert.Equal(sent.Snapshot, acknowledged.Snapshot);
            Assert.True(sent.Snapshot > 0); Assert.Equal(session.Id, acknowledged.Session);
            Assert.Single(trace.Select(entry => entry.Run).Distinct());
            Assert.Contains(trace.SelectMany(entry => entry.Menu ?? []), line => line.Text == "助理");
            var serialized = System.Text.Json.JsonSerializer.Serialize(trace, TraceJsonOptions);
            Assert.DoesNotContain("虚构口令", serialized); Assert.DoesNotContain("私有步骤名称", serialized);
            Assert.DoesNotContain("虚构私有账号名", serialized);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("capture", "capture")]
    [InlineData("recognition", "recognition")]
    [InlineData("controller-connect", "controller-connect")]
    [InlineData("private-native-detail", "unknown")]
    public async Task ScriptTracePreservesSafeNativeFailureCodes(string nativeCode, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-trace-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var launcher = new LauncherModule(new SqliteGameProfileRepository(Path.Combine(root, "fake.db")), new FakeRuntime());
            await launcher.InitializeAsync();
            var session = (await launcher.StartAutomationSessionAsync((await launcher.CreateProfileAsync("虚构诊断失败")).Id))!;
            var trace = new List<ScriptTraceEvent>();
            var vision = new MenuVision { FailureReads = [1, 2], FailureCode = nativeCode };
            var definition = new ScriptDefinition { Entry = "click", Flow = [new() { Id = "click", Type = "click", X = 125, Y = 25 }] };
            await Assert.ThrowsAsync<InvalidOperationException>(() => new OfficialScriptExecutor(vision, trace: trace.Add)
                .ExecuteAsync(session, definition, new() { SaveNumber = 5 }, SpeedMultiplier.Create(2), (_, _) => { }, default));
            Assert.Equal(expected, Assert.Single(trace, entry => entry.Event == "observe-error").Result);
            Assert.DoesNotContain("private-native-detail", System.Text.Json.JsonSerializer.Serialize(trace));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TakeoverCanExitWorldThroughObservedSystemGear(bool rightFooterExit)
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-takeover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new SqliteGameProfileRepository(Path.Combine(root, "fake.db"));
            await using var launcher = new LauncherModule(repository, new FakeRuntime());
            await launcher.InitializeAsync();
            var profile = await launcher.CreateProfileAsync("虚构接管账号");
            var session = (await launcher.StartAutomationSessionAsync(profile.Id))!;
            var vision = new MenuVision { State = "world", RightFooterExit = rightFooterExit };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await new OfficialScriptExecutor(vision).ExitSaveAsync(session, (_, _) => { }, timeout.Token);
            Assert.Equal("home", vision.State);
            Assert.Equal(TakeoverActions, vision.Actions);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ReadsNamesForAllEightCardsInRowOrder(int incompleteFrames)
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new SqliteGameProfileRepository(Path.Combine(root, "fake.db"));
            await using var launcher = new LauncherModule(repository, new FakeRuntime());
            await launcher.InitializeAsync();
            var profile = await launcher.CreateProfileAsync("虚构流程账号");
            var session = (await launcher.StartAutomationSessionAsync(profile.Id))!;
            var vision = new MenuVision { IncompleteFrames = incompleteFrames };
            var executor = new OfficialScriptExecutor(vision);
            var saves = await executor.ReadSavesAsync(session, _ => { }, default);
            Assert.Equal(Enumerable.Range(1, 8), saves.Select(save => save.Number));
            Assert.Equal(Enumerable.Range(1, 8).Select(number => number == 1 ? "123456 lv.99" : "测试档" + number + " lv.99"), saves.Select(save => save.Name));
            Assert.Equal("selection", vision.State);
            Assert.Equal("读取存档", Assert.Single(vision.Actions));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task IncompleteMenuWaitCanStopWithoutClickingAnySave()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new SqliteGameProfileRepository(Path.Combine(root, "fake.db"));
            await using var launcher = new LauncherModule(repository, new FakeRuntime());
            await launcher.InitializeAsync();
            var profile = await launcher.CreateProfileAsync("虚构未绘制菜单");
            var session = (await launcher.StartAutomationSessionAsync(profile.Id))!;
            var vision = new MenuVision { IncompleteFrames = int.MaxValue };
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new OfficialScriptExecutor(vision).ReadSavesAsync(session, _ => { }, cancel.Token));
            Assert.Equal(0, vision.SelectedSave);
            Assert.Equal("读取存档", Assert.Single(vision.Actions));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Fact]
    public async Task DailyConfirmsHeaderCheckWithLastSaveRowOutsideViewportThenExits()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new SqliteGameProfileRepository(Path.Combine(root, "fake.db"));
            await using var launcher = new LauncherModule(repository, new FakeRuntime());
            await launcher.InitializeAsync();
            var profile = await launcher.CreateProfileAsync("虚构流程账号");
            var session = (await launcher.StartAutomationSessionAsync(profile.Id))!;
            var vision = new MenuVision { IncompleteFrames = 2 };
            var executor = new OfficialScriptExecutor(vision);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var script = new ScriptDefinition { Id = "official-daily", Entry = "daily", Flow = [new() { Id = "daily", Type = "officialDaily" }] };
            var timer = Stopwatch.StartNew();
            await executor.ExecuteAsync(session, script, new() { ProfileId = profile.Id, SaveNumber = 8 },
                SpeedMultiplier.Create(2), (_, _) => { }, timeout.Token);
            Assert.True(timer.Elapsed >= TimeSpan.FromSeconds(3));
            Assert.Equal(8, vision.SelectedSave);
            Assert.Equal("saved", vision.State);
            Assert.DoesNotContain("保存存档", vision.Actions);
            await executor.ExitSaveAsync(session, (_, _) => { }, timeout.Token);
            Assert.Equal("home", vision.State);
            Assert.Equal(ExpectedActions, vision.Actions);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class FakeRuntime : IAutomationGameRuntime
    {
        public Task<GamePageResolution> ResolveGamePageAsync() => throw new NotSupportedException();
        public Task<IGameSession> StartAsync(GameProfile profile, CancellationToken token = default) => Task.FromResult<IGameSession>(new FakeSession(profile.Id));
        public Task<IGameSession> StartAutomationAsync(GameProfile profile, CancellationToken token = default) => StartAsync(profile, token);
    }
    private sealed class FakeSession(Guid profile) : IAutomationGameSession
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid Id { get; } = Guid.NewGuid();
        public Guid ProfileId => profile;
        public Task Completion => _completion.Task;
        public void Activate() { }
        public Task CloseAsync(CancellationToken token = default) { _completion.TrySetResult(); return Task.CompletedTask; }
        public bool WindowLocked { get; private set; } = true;
        public Task AcquireWindowLockAsync(CancellationToken token = default) => System.Threading.Tasks.Task.CompletedTask;
        public Task ReleaseWindowLockAsync(CancellationToken token = default) { WindowLocked = false; return System.Threading.Tasks.Task.CompletedTask; }
        public Task<AutomationWindowTarget> GetTargetAsync(CancellationToken token = default)
            => Task.FromResult(new AutomationWindowTarget(Id, profile, 1, 2, 3, 950, 600));
        public Task<SpeedMultiplier> ApplySpeedAsync(SpeedMultiplier speed, CancellationToken token = default) => Task.FromResult(SpeedMultiplier.Original);
    }

    private sealed class MenuVision : IScriptVision
    {
        public bool BagFull { get; init; }
        public bool GenesFull { get; init; }
        public bool NothingToDismantle { get; init; }
        public bool UnknownDismantleResult { get; init; }
        public bool ActualInventoryResult { get; init; }
        public bool HideInventoryDialogText { get; init; }
        public bool InventoryTooltips { get; init; }
        private bool _inventoryHoverCleared;
        public List<(int X, int Y)> InventoryHoverPoints { get; } = [];
        public int InventoryDialogOcrReads { get; private set; }
        public List<(int X, int Y)> InventoryDialogClicks { get; } = [];
        public bool DelayedInventoryResult { get; init; }
        private int _inventoryResultFrames;
        public bool BagTidied { get; private set; }
        public bool TidiedBeforeMovement { get; private set; } = true;
        public List<string> InventoryActions { get; } = [];
        public int HoverAppearsAfter { get; init; } = 1;
        public bool QuotaFull { get; init; }
        public bool SplitQuotaDigits { get; init; }
        public bool HideQuotaLabel { get; init; }
        public List<long> CounterSnapshots { get; } = [];
        public long LastQuotaSnapshot { get; private set; }
        public bool HideReplayLabel { get; init; }
        public (int X, int Y)? ReplayPoint { get; private set; }
        public (int X, int Y)? ReplayConfirmPoint { get; private set; }
        public int QuotaReads { get; private set; }
        private bool _quotaVisible;
        public Task MovePointerAsync(AutomationWindowTarget target, int x, int y, CancellationToken token)
        {
            // 地图悬停框会遮住关卡名；离开卡片后才恢复识别，不把这次移鼠标算作限额查看。
            if (State.StartsWith("inventory-", StringComparison.Ordinal))
            {
                InventoryHoverPoints.Add((x, y));
                _inventoryHoverCleared = x == 720 && y == 32;
            }
            else if (State == "city") { _mapTooltipDismissed = true; MapTooltipDismissals++; }
            else { QuotaReads++; _quotaVisible = QuotaReads >= HoverAppearsAfter; }
            return Task.CompletedTask;
        }
        public bool MapTooltipCoversTitle { get; init; }
        public bool DifficultyTooltipCoversLabels { get; init; }
        public int DifficultyLabelReads { get; private set; }
        public (int X, int Y)? DifficultyClick { get; private set; }
        public int MapTooltipDismissals { get; private set; }
        public int MapTitleReads { get; private set; }
        public (int X, int Y)? LevelClick { get; private set; }
        private bool _mapTooltipDismissed;
        public Action? OnFirstRead { get; init; }
        public int[] FailureReads { get; init; } = [];
        public string FailureCode { get; init; } = "vision";
        public int ReadCount { get; private set; }
        public int Releases { get; private set; }
        public string? FarmMode { get; init; }
        public bool IgnoreFirstModeClick { get; init; }
        public int DelayDifficultyFrames { get; init; }
        public string? ExtraOcr { get; init; }
        private int _difficultyFrames;
        private int _modeClicks;
        public Action? OnDifficultySelected { get; init; }
        public bool MissingSecondArrow { get; init; }
        public bool HideAllArrows { get; init; }
        public bool ShowRewardBanner { get; init; }
        public string BattleWaveText { get; init; } = "7/7 01:35";
        public string[]? ReplayHud { get; init; }
        public int Replays { get; private set; }
        public int ReplayHudReads { get; private set; }
        private string _replayHud = "";
        public Action? OnThirdBattleFrame { get; init; }
        public int BattleFrames { get; private set; }
        public bool DropWaveDigitAfterClear { get; init; }
        public bool PlayerAtSurveyPoint { get; init; }
        public bool CornerMaterial { get; init; }
        public bool VisibleMaterialDuringRise { get; init; }
        public bool StartAtLeftEdge { get; init; }
        public bool FlickerFirstPickupReview { get; init; }
        public bool ExposeSecondMaterial { get; init; }
        public bool HideHealthUntilRight { get; init; }
        public bool SecondMaterialCollected { get; private set; }
        public bool EarlyMaterialCollected { get; private set; }
        public bool CornerMaterialCollected { get; private set; }
        public bool HidePickupHealth { get; init; }
        public bool ShowYellowMapDot { get; init; }
        public bool LevelBarAfterAiOff { get; init; }
        public string WaveAfterClear { get; init; } = "/7 01:35";
        public Action? OnMovement { get; init; }
        public Action? OnInitialSweepObservation { get; init; }
        private int _offFrames;
        public Action? MovementObserved { get; set; }
        public bool RoundTenHasMaterial { get; init; }
        public Action? OnBattleSystemOpened { get; init; }
        public int Movements { get; private set; }
        public List<char> MovementKeys { get; } = [];
        public List<int> MovementDurations { get; } = [];
        public bool SupportsBattleInput => FarmMode is not null;
        public bool SupportsDiagonalInput => SupportsBattleInput;
        public int IncompleteFrames { get; init; }
        public bool FastDaily { get; init; }
        public string[] DailyNames { get; init; } = ["日常操作复制"];
        private string _dailyName = "日常操作复制";
        public bool AiEnabled { get; private set; } = true;
        public List<bool> AiChanges { get; } = [];
        public bool SupportsBattleAi => SupportsBattleInput;
        public bool SupportsBattleAiRise => SupportsBattleInput;
        public bool SupportsQuotaCounter => SupportsBattleInput;
        public int CombinedAiRiseCount { get; private set; }
        public Task ToggleBattleAiAsync(AutomationWindowTarget target, CancellationToken token)
        { AiEnabled = !AiEnabled; AiChanges.Add(AiEnabled); return Task.CompletedTask; }
        public Task ToggleBattleAiAndRiseAsync(AutomationWindowTarget target, int milliseconds, CancellationToken token)
        {
            AiEnabled = !AiEnabled; AiChanges.Add(AiEnabled); CombinedAiRiseCount++;
            Move('W', milliseconds); return RecordMovement('W', milliseconds, token);
        }
        public bool RightFooterExit { get; init; }
        public bool SaveOnlyMenu { get; init; }
        public bool HideSystemOnFirstRead { get; init; }
        public bool SidebarExpanded { get; set; }
        public int SidebarCollapseDelayFrames { get; init; }
        private int _sidebarClosing;
        private int _menuFrames;
        public string State { get; set; } = "home";
        public List<string> Actions { get; } = [];
        public int SelectedSave { get; private set; }
        private long _snapshot;
        private long _captured;
        private int _progress;
        private double _heroOffsetX, _heroOffsetY;
        private bool _battleHudRead;
        public Task BeginAsync(CancellationToken token) => Task.CompletedTask;
        public Task EndAsync() => Task.CompletedTask;
        public Task ReleaseTargetAsync(AutomationWindowTarget target) { Releases++; return Task.CompletedTask; }
        public Task<ScriptRect?> FindImageAsync(AutomationWindowTarget target, ScriptRect roi, byte[] image, CancellationToken token) => throw new NotSupportedException();

        public Task<ScriptVisionFrame> ReadAsync(AutomationWindowTarget target, ScriptRect roi, CancellationToken token, long snapshotId = 0, ScriptTextStyle style = ScriptTextStyle.Normal)
        {
            if (State.StartsWith("inventory-", StringComparison.Ordinal) && (roi == new ScriptRect(255, 135, 440, 155)
                || roi == new ScriptRect(360, 290, 240, 65))) InventoryDialogOcrReads++;
            if (State == "city" && roi == new ScriptRect(350, 150, 550, 400)) MapTitleReads++;
            if (State == "difficulty" && roi == new ScriptRect(250, 170, 450, 325)) DifficultyLabelReads++;
            if (++ReadCount == 1) OnFirstRead?.Invoke();
            if (FailureReads.Contains(ReadCount)) throw SharedScriptVision.CreateFailure(FailureCode);
            if (snapshotId == 0) { _snapshot++; _captured = Stopwatch.GetTimestamp(); }
            if (snapshotId == 0 && _sidebarClosing > 0 && --_sidebarClosing == 0) SidebarExpanded = false;
            var lines = new List<ScriptText>();
            if (ExtraOcr is not null) lines.Add(new(ExtraOcr, new(400, 20, 100, 20), 0.99));
            if (State == "difficulty" && snapshotId == 0 && ++_difficultyFrames <= DelayDifficultyFrames)
                return Task.FromResult(new ScriptVisionFrame(_snapshot, _captured, lines));
            if (State == "battle" && snapshotId == 0 && roi == new ScriptRect(800, 0, 150, 75)) _battleHudRead = true;
            if (HideSystemOnFirstRead && ReadCount == 1)
                return Task.FromResult(new ScriptVisionFrame(_snapshot, _captured, lines));
            void Add(string text, ScriptRect box) { if (style == ScriptTextStyle.Green || Intersects(roi, box)) lines.Add(new(text, box, 0.99)); }
            if (style == ScriptTextStyle.Green) Add("保存存档", new(42, 9, 180, 42));
            else if (State == "home") Add("读取存档", new(410, 550, 100, 25));
            else if (State == "selection")
            {
                Add("读取存档", new(400, 150, 160, 30));
                if (roi.Width <= 182 && roi.Height <= 40)
                {
                    var row = (roi.Y - 198) / 61;
                    var number = row * 2 + (roi.X > 400 ? 2 : 1);
                    if (style != ScriptTextStyle.SaveName) Add("角色通关", new(roi.X + 130, roi.Y, 44, 12));
                    Add((number == 1 ? "123456" : "测试档" + number) + " 1v.99", new(roi.X + 5, roi.Y + 8, 110, 12));
                    if (style != ScriptTextStyle.SaveName) Add("2026-01-01 12:00:00", new(roi.X + 5, roi.Y + 30, 130, 8));
                }
            }
            else if (State == "world")
            {
                Add("助理", new(100, 45, 50, 24));
                if (SidebarExpanded) Add("√日常操作复制23/23", new(796, 171, 111, 14));
                else if (FarmMode is not null) Add("寒光市", new(780, 410, 60, 20));
            }
            else if (State == "city")
            {
                if (!MapTooltipCoversTitle || _mapTooltipDismissed) Add("天空顶", new(640, 430, 60, 20));
                else Add("正常模式", new(410, 295, 100, 20));
            }
            else if (State == "mode")
            {
                Add("天空顶", new(450, 119, 60, 20)); Add(FarmMode!, new(343, 174, 67, 24));
            }
            else if (State == "difficulty")
            {
                Add("选择难度", new(400, 135, 100, 25));
                if (!DifficultyTooltipCoversLabels) Add("地狱四", new(560, 390, 60, 25));
            }
            else if (State == "battle")
            {
                if (BagFull && !BagTidied) Add("满", new(65, 524, 25, 25));
                if (Replays > 0 && snapshotId == 0 && roi == new ScriptRect(800, 0, 150, 75))
                    _replayHud = ReplayHud![Math.Min(ReplayHudReads++, ReplayHud.Length - 1)];
                Add(Replays > 0 ? _replayHud : DropWaveDigitAfterClear && BattleFrames > 0 ? WaveAfterClear : BattleWaveText, new(830, 12, 90, 15));
                if (ShowRewardBanner) Add("通关抽奖获得", new(280, 17, 75, 17));
            }
            else if (State.StartsWith("inventory-", StringComparison.Ordinal))
            {
                Add("背包", new(700, 25, 60, 25));
                // 保留标题但遮住分类和拆解区，模拟现场大块详情框；每次操作后鼠标仍可能触发悬停。
                if (InventoryTooltips && !_inventoryHoverCleared)
                    return Task.FromResult(new ScriptVisionFrame(_snapshot, _captured, lines));
                if (State is "inventory-weapon" or "inventory-armor" or "inventory-genes")
                {
                    Add("武器", new(560, 59, 55, 25)); Add("装备", new(630, 59, 55, 25)); Add("基因体", new(752, 59, 70, 25));
                    Add(State == "inventory-genes" ? "一键分解" : "一键拆解", new(730, 472, 100, 25));
                    if (GenesFull) Add("满", new(770, 29, 25, 25));
                }
                else if (HideInventoryDialogText) { }
                else if (State.EndsWith("-confirm", StringComparison.Ordinal))
                {
                    Add(State == "inventory-genes-confirm" ? "你确定要一键分解基因体？" : "你确定要一键拆解指定条件的物品？", new(300, 240, 340, 24));
                    Add("确定", new(402, 305, 35, 24)); Add("取消", new(515, 305, 35, 24));
                }
                else if (DelayedInventoryResult && snapshotId == 0 && ++_inventoryResultFrames <= 2)
                {
                    Add("你确定要一键拆解指定条件的物品？", new(300, 240, 340, 24));
                    Add("确定", new(402, 305, 35, 24)); Add("取消", new(515, 305, 35, 24));
                }
                else if (!UnknownDismantleResult)
                {
                    if (NothingToDismantle) Add("没有可用于一键拆解的物品。", new(310, 200, 330, 25));
                    else if (ActualInventoryResult) { Add("拆解完成！", new(437, 193, 68, 21)); Add("已拆解物品85个。", new(409, 213, 124, 20)); }
                    else { Add("拆解成功", new(310, 200, 150, 25)); Add("获得零件", new(310, 230, 150, 25)); }
                    Add("确定", new(458, 305, 35, 24));
                }
            }
            else if (State == "battle-system")
            {
                // 故意不提供灰色“退出存档”，关卡菜单应以可用的重玩/退出项确认。
                if (!HideReplayLabel) Add("重玩关卡", new(400, 295, 120, 25));
                Add("退出关卡", new(400, 340, 120, 25));
                if (!HideQuotaLabel) Add("掉落查看", new(110, 475, 90, 20));
                if (_quotaVisible)
                {
                    Add(QuotaFull ? "冷冻宝石 205/205" : "冷冻宝石 22/205", new(100, 525, 140, 20));
                    LastQuotaSnapshot = _snapshot;
                    if (SplitQuotaDigits)
                    {
                        Add("冥王图鉴", new(100, 555, 70, 20));
                        if (style == ScriptTextStyle.QuotaCounter)
                        {
                            CounterSnapshots.Add(snapshotId);
                            Add("(3/27]", new(175, 557, 55, 15));
                        }
                    }
                    else Add(QuotaFull ? "冥王图鉴 27/27" : "冥王图鉴 3/27", new(100, 555, 140, 20));
                }
            }
            else if (State == "battle-exit")
            {
                Add("是否退出关卡", new(300, 180, 200, 30)); Add("确定", new(380, 300, 60, 30));
            }
            else if (State == "battle-replay")
            {
                Add("是否重玩关卡", new(300, 180, 200, 30)); Add("确定", new(380, 300, 60, 30));
            }
            else if (State == "level-result")
            {
                Add("胜利", new(457, 57, 35, 20)); Add("综合评分：5星", new(427, 176, 92, 18));
            }
            else if (State == "assistant")
            {
                for (var i = 0; i < DailyNames.Length; i++) Add(DailyNames[i], new(130, 180 + i * 35, 140, 24));
            }
            else if (State == "selected")
            {
                Add(_dailyName, new(350, 130, 140, 24)); Add("运行", new(530, 490, 60, 24));
            }
            else if (State == "prompt")
            {
                Add("是否开始运行《" + _dailyName + "》？", new(290, 180, 380, 40)); Add("确定", new(350, 300, 60, 30));
            }
            else if (State is "running" or "saved")
            {
                if (roi == new ScriptRect(759, 154, 182, 420))
                {
                    _progress++;
                    // 首次截图可能已经到22/23，只剩官方脚本等待保存冷却，然后直接完成。
                    var done = FastDaily ? _progress == 1 ? 22 : 23 : _progress < 3 ? _progress - 1 : 23;
                    Add("√" + _dailyName + done + "/23", new(786, 170, 122, 17));
                    // 实际完成画面末行在侧栏视口外，只能看到标题23/23及左侧绿勾。
                    if (done == 23) State = "saved";
                }
                // 城镇现场按钮在系统菜单右下角，旧ROI的右边界760恰好将它排除。
                else if (SaveOnlyMenu)
                {
                    Add("系统", new(455, 38, 40, 20)); Add("保存存档", new(420, 225, 100, 25));
                    Add("单人模式", new(420, 275, 100, 25));
                }
                else Add("退出存档", RightFooterExit ? new(764, 435, 53, 18) : new(400, 300, 100, 25));
            }
            else if (State == "exit")
            {
                Add("是否退出存档", new(300, 180, 200, 30)); Add("确定", new(360, 300, 60, 30));
            }
            return Task.FromResult(new ScriptVisionFrame(_snapshot, _captured, lines));
        }

        public Task<ScriptPixels> PixelsAsync(AutomationWindowTarget target, ScriptRect roi, long snapshot, CancellationToken token)
        {
            using var bitmap = new Bitmap(roi.Width, roi.Height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
                if (State.StartsWith("inventory-", StringComparison.Ordinal))
                {
                    var confirmation = State.EndsWith("-confirm", StringComparison.Ordinal)
                        || DelayedInventoryResult && State.EndsWith("-result", StringComparison.Ordinal) && _inventoryResultFrames <= 2;
                    var result = State.EndsWith("-result", StringComparison.Ordinal) && !UnknownDismantleResult;
                    if (confirmation || result)
                    {
                        using var dialog = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Fixtures",
                            confirmation ? "sky-top-inventory-confirm.png" : NothingToDismantle ? "sky-top-inventory-empty.png" : "sky-top-inventory-result.png"));
                        graphics.DrawImageUnscaled(dialog, 255 - roi.X, 135 - roi.Y);
                    }
                }
                if (State == "battle-system" && _quotaVisible && QuotaFull)
                { graphics.FillRectangle(Brushes.Lime, 100, 525, 140, 20); graphics.FillRectangle(Brushes.Lime, 100, 555, 140, 20); }
                if (State == "battle")
                {
                    graphics.Clear(Color.FromArgb(15, 23, 90));
                    graphics.FillRectangle(Brushes.Orange, 60, 10, 130, 10);
                    if (AiEnabled) graphics.FillRectangle(Brushes.Lime, 8, 8, 6, 10);
                    // 实机中角色飞到顶部后，绿色等级/血条会进入AI检测范围。
                    else if (LevelBarAfterAiOff) graphics.FillRectangle(Brushes.Lime, 8, 7, 8, 3);
                    if (!AiEnabled && ++_offFrames == 2) OnInitialSweepObservation?.Invoke();
                    if (RoundTenHasMaterial && Replays >= 9)
                    {
                        using var material = new Bitmap(Path.Combine(SkyTopDropVision.ResourceDirectory, "guard-gem.png"));
                        graphics.DrawImageUnscaled(material, 350, 250);
                    }
                    if (CornerMaterial && !CornerMaterialCollected)
                    {
                        using var material = new Bitmap(Path.Combine(SkyTopDropVision.ResourceDirectory, "guard-gem.png"));
                        graphics.DrawImageUnscaled(material, 838, 45);
                    }
                    if (VisibleMaterialDuringRise && !EarlyMaterialCollected && !(FlickerFirstPickupReview && _offFrames is 3 or 4))
                    {
                        using var material = new Bitmap(Path.Combine(SkyTopDropVision.ResourceDirectory, "hades-codex.png"));
                        graphics.DrawImageUnscaled(material, 480, 160);
                    }
                    if (ExposeSecondMaterial && EarlyMaterialCollected && !SecondMaterialCollected)
                    {
                        using var material = new Bitmap(Path.Combine(SkyTopDropVision.ResourceDirectory, "hades-codex.png"));
                        graphics.DrawImageUnscaled(material, 650, 240);
                    }
                    if (DropWaveDigitAfterClear && (!HidePickupHealth || AiEnabled)
                        && (!HideHealthUntilRight || AiEnabled || _heroOffsetX >= 400))
                    {
                        using var health = new SolidBrush(Color.Lime);
                        // 菜单回归提供已完整可见的无材料掉落区，不靠任意空白帧宣告拾取结束。
                        graphics.FillRectangle(health, (float)((StartAtLeftEdge ? 10 : PlayerAtSurveyPoint ? 554 : 300) + _heroOffsetX),
                            (float)((PlayerAtSurveyPoint ? 100 : 250) + _heroOffsetY), 40, 3);
                        if (PlayerAtSurveyPoint)
                        {
                            using var first = new Bitmap(Path.Combine(SkyTopDropVision.ResourceDirectory, "offhand-moon-spike.png"));
                            using var second = new Bitmap(Path.Combine(SkyTopDropVision.ResourceDirectory, "offhand-butcher.png"));
                            graphics.DrawImageUnscaled(first, 360, 200); graphics.DrawImageUnscaled(second, 430, 200);
                        }
                    }
                    if (ShowYellowMapDot)
                    {
                        var baseX = StartAtLeftEdge ? 30 : PlayerAtSurveyPoint ? 574 : 320; var baseY = PlayerAtSurveyPoint ? 148 : 298;
                        graphics.FillRectangle(Brushes.Yellow, (float)(831 + (baseX + _heroOffsetX) * 72 / 910),
                            (float)(20 + (baseY + _heroOffsetY - 60) * 40 / 390), 3, 3);
                        graphics.FillRectangle(Brushes.Lime, 850, 42, 3, 3);
                    }
                    if (_battleHudRead) { BattleFrames++; _battleHudRead = false; }
                    if (!HideAllArrows && (!MissingSecondArrow || BattleFrames != 2))
                    {
                        using var gold = new SolidBrush(Color.FromArgb(230, 160, 10));
                        graphics.FillRectangle(gold, 650, 310, 25, 16);
                        for (var offset = 0; offset < 25; offset++) graphics.FillRectangle(gold, 674 + offset, 298 + offset / 2, 1, 40 - offset * 3 / 2);
                    }
                    if (BattleFrames == 3) OnThirdBattleFrame?.Invoke();
                }
                else if (roi.Width == 950 && ++_menuFrames > IncompleteFrames)
                {
                    using var border = new Pen(Color.FromArgb(80, 80, 80));
                    for (var row = 0; row < 4; row++) for (var column = 0; column < 2; column++)
                        graphics.DrawRectangle(border, column == 0 ? 276 : 483, 191 + row * 61, 199, 54);
                }
                else
                {
                    using var check = new Pen(Color.Lime);
                    graphics.DrawLines(check, [new Point(29, 24), new Point(32, 28), new Point(38, 16)]);
                }
            }
            var data = bitmap.LockBits(new(0, 0, roi.Width, roi.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var pixels = new byte[roi.Width * roi.Height * 3];
            try { for (var y = 0; y < roi.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * roi.Width * 3, roi.Width * 3); }
            finally { bitmap.UnlockBits(data); }
            return Task.FromResult(new ScriptPixels(roi.Width, roi.Height, pixels));
        }

        public Task ClickAsync(AutomationWindowTarget target, int x, int y, CancellationToken token)
        {
            if (InventoryTooltips && State.StartsWith("inventory-", StringComparison.Ordinal))
            {
                if (!_inventoryHoverCleared) throw new InvalidOperationException("物品提示尚未清除，不能点击被遮挡的背包操作");
                _inventoryHoverCleared = false;
            }
            if (State.StartsWith("inventory-", StringComparison.Ordinal) && (State.EndsWith("-confirm", StringComparison.Ordinal)
                || State.EndsWith("-result", StringComparison.Ordinal))) InventoryDialogClicks.Add((x, y));
            switch (State)
            {
                case "home": Actions.Add("读取存档"); State = "selection"; break;
                case "selection":
                    SelectedSave = (y - 218) / 61 * 2 + (x > 480 ? 2 : 1); Actions.Add("存档" + SelectedSave); State = "world"; break;
                case "world":
                    if (SidebarExpanded && x == 937 && y == 178)
                    {
                        Actions.Add("收起日常侧栏"); _sidebarClosing = SidebarCollapseDelayFrames;
                        if (_sidebarClosing == 0) SidebarExpanded = false;
                    }
                    else if (FarmMode is not null) { Actions.Add("寒光市"); State = "city"; }
                    else if (x == 742 && y == 565) { Actions.Add("系统"); State = "saved"; }
                    else { Actions.Add("助理"); State = "assistant"; }
                    break;
                case "city": LevelClick = (x, y); Actions.Add("天空顶"); State = "mode"; break;
                case "mode":
                    Actions.Add(FarmMode!);
                    if (!IgnoreFirstModeClick || ++_modeClicks > 1) State = "difficulty";
                    break;
                case "difficulty": DifficultyClick = (x, y); Actions.Add("地狱四"); State = "battle"; OnDifficultySelected?.Invoke(); break;
                case "battle":
                    if (BagFull && !BagTidied && x == 80 && y == 575)
                    { InventoryActions.Add("打开背包"); State = "inventory-genes"; break; }
                    if (x != 29 || y != 574) throw new InvalidOperationException("战后必须点关卡齿轮，不能点城镇位置");
                    Actions.Add("关卡系统"); State = "battle-system"; OnBattleSystemOpened?.Invoke(); break;
                case "assistant": _dailyName = DailyNames[(y - 180) / 35]; Actions.Add(_dailyName); State = "selected"; break;
                case "battle-system":
                    if (x == 825 && y == 44) { Actions.Add("关闭关卡系统"); State = "battle"; _quotaVisible = false; }
                    else if (ReplayHud is not null && y is >= 295 and <= 325) { ReplayPoint = (x, y); Actions.Add("重玩关卡"); State = "battle-replay"; }
                    else { Actions.Add("退出关卡"); State = "battle-exit"; }
                    break;
                case "battle-replay": ReplayConfirmPoint = (x, y); Actions.Add("确定重玩关卡"); Replays++; _heroOffsetX = _heroOffsetY = 0; State = "battle"; break;
                case "battle-exit": Actions.Add("确定退出关卡"); State = "level-result"; break;
                case "level-result":
                    if (x != 691 || y != 60) throw new InvalidOperationException("只关闭已识别的评价右上角");
                    Actions.Add("关闭评价"); State = "world"; break;
                case "selected": Actions.Add("运行"); State = "prompt"; break;
                case "prompt": Actions.Add("确定运行"); State = "running"; break;
                case "saved":
                    if (x == 825 && y == 44) { Actions.Add("关闭系统"); State = "world"; }
                    else { Actions.Add("退出存档"); State = "exit"; }
                    break;
                case "exit": Actions.Add("确定退出"); State = "home"; break;
                case "inventory-weapon":
                case "inventory-armor":
                case "inventory-genes":
                    if (x == 880 && y == 32) { InventoryActions.Add("关闭背包"); BagTidied = true; State = "battle"; }
                    else if (y < 100)
                    {
                        State = x < 620 ? "inventory-weapon" : x < 710 ? "inventory-armor" : "inventory-genes";
                        InventoryActions.Add(State == "inventory-weapon" ? "武器" : State == "inventory-armor" ? "装备" : "基因体");
                    }
                    else { InventoryActions.Add(State == "inventory-weapon" ? "一键拆解武器" : State == "inventory-armor" ? "一键拆解装备" : "一键分解"); State += "-confirm"; }
                    break;
                case "inventory-weapon-confirm": InventoryActions.Add("确认拆解武器"); State = "inventory-weapon-result"; break;
                case "inventory-armor-confirm": InventoryActions.Add("确认拆解装备"); State = "inventory-armor-result"; break;
                case "inventory-genes-confirm": InventoryActions.Add("确认分解"); State = "inventory-genes"; break;
                case "inventory-weapon-result": InventoryActions.Add("确认武器结果"); State = "inventory-weapon"; break;
                case "inventory-armor-result": InventoryActions.Add("确认装备结果"); State = "inventory-armor"; break;
                default: throw new InvalidOperationException("合成状态不应重复点击");
            }
            return Task.CompletedTask;
        }
        public Task HoldKeyAsync(AutomationWindowTarget target, char key, int milliseconds, CancellationToken token)
        { Move(key, milliseconds); return RecordMovement(key, milliseconds, token); }
        public Task HoldKeysAsync(AutomationWindowTarget target, char first, char second, int milliseconds, CancellationToken token)
        { Move(first, milliseconds, second); return RecordMovement(first, milliseconds, token); }
        private void Move(char key, int milliseconds, char? second = null)
        {
            var baseX = StartAtLeftEdge ? 30 : PlayerAtSurveyPoint ? 574 : 320; var baseY = PlayerAtSurveyPoint ? 148 : 298;
            var beforeX = baseX + _heroOffsetX; var beforeY = baseY + _heroOffsetY;
            _heroOffsetX += key == 'D' ? milliseconds * .5 : key == 'A' ? -milliseconds * .5 : 0;
            _heroOffsetY += key == 'S' ? milliseconds * .5 : key == 'W' ? -milliseconds * .5 : 0;
            _heroOffsetX += second == 'D' ? milliseconds * .5 : second == 'A' ? -milliseconds * .5 : 0;
            _heroOffsetY += second == 'S' ? milliseconds * .5 : second == 'W' ? -milliseconds * .5 : 0;
            // 独立场景夹具：按键持续到地图边界后不再移动，跟随角色出生点而非固定出口矩形。
            _heroOffsetX = Math.Clamp(baseX + _heroOffsetX, 30, 910) - baseX;
            _heroOffsetY = Math.Clamp(baseY + _heroOffsetY, 60, 450) - baseY;
            if (CornerMaterial)
            {
                // 靠近材料才真正移除图案，防止只跑完动作、实际没有拾取的实现通过回归。
                if (Passed(850, 60))
                    CornerMaterialCollected = true;
            }
            if (VisibleMaterialDuringRise)
            {
                if (Passed(492, 174))
                    EarlyMaterialCollected = true;
            }
            if (ExposeSecondMaterial && EarlyMaterialCollected)
            {
                if (Passed(662, 254))
                    SecondMaterialCollected = true;
            }
            // 斜向输入按真实线段计算拾取，不能把线段外接矩形里的物品都当成已捡。
            bool Passed(double targetX, double targetY)
            {
                var dx = baseX + _heroOffsetX - beforeX; var dy = baseY + _heroOffsetY - beforeY;
                var length = dx * dx + dy * dy;
                var t = length == 0 ? 0 : Math.Clamp(((targetX - beforeX) * dx + (targetY - beforeY) * dy) / length, 0, 1);
                return Math.Pow(beforeX + t * dx - targetX, 2) + Math.Pow(beforeY + t * dy - targetY, 2) <= 24 * 24;
            }
        }
        private Task RecordMovement(char key, int milliseconds, CancellationToken token)
        { if (BagFull && !BagTidied) TidiedBeforeMovement = false; Movements++; MovementKeys.Add(key); MovementDurations.Add(milliseconds); OnMovement?.Invoke(); MovementObserved?.Invoke(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        private static bool Intersects(ScriptRect a, ScriptRect b) => a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
    }
}
