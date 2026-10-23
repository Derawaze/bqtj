using System.Diagnostics;
using System.Text.RegularExpressions;
using BqtjLauncher.Application;
using BqtjLauncher.Domain;

namespace BqtjLauncher.Runtime.Flash;

public sealed partial class OfficialScriptExecutor
{
    public bool SupportsDailyMaterialFarming => Environment.Is64BitOperatingSystem && Vision.SupportsDiagonalInput && Vision.SupportsBattleAiRise && Vision.SupportsQuotaCounter;
    public string DailyMaterialUnavailableReason => "天空顶刷材料需要64位Windows及配套协议7视觉组件，请将组件包解压到启动器目录。";
    /// <summary>刷关适配复用已互斥接管的会话；倍率和尺寸锁仍由外层统一恢复。</summary>
    public async Task FarmDailyMaterialsAsync(AutomationGameSession session, DailyMaterialFarmOptions options,
        Action<string, ScriptRunState> stage, CancellationToken token)
    {
        if (!SupportsDailyMaterialFarming) throw new NotSupportedException(DailyMaterialUnavailableReason);
        var context = Create(session, stage, token);
        context.Trace("farming-start", result: options.HasPresident ? "president" : "manual-pickup");
        try
        {
            await new DailyMaterialFarming(new SkyTopBattle(context, Vision,
                    options.HasPresident ? null : SkyTopDropVision.Load(options.Map), options.HasPresident))
                .RunAsync(options, value => context.Stage(value, "刷关调度"), token).ConfigureAwait(false);
            context.Trace("farming-complete");
        }
        catch (Exception error) { context.Trace("farming-ended", result: error.GetType().Name); throw; }
    }

    /// <summary>地图/重玩/限额属于游戏UI；战斗由内置AI执行，后台输入只用于战后拾取。</summary>
    private sealed class SkyTopBattle(Context context, IScriptVision vision, SkyTopDropVision.Catalog? drops, bool hasPresident) : IDailyMaterialBattle
    {
        private static readonly ScriptRect Full = new(0, 0, 950, 600);
        private static readonly ScriptRect Hud = new(800, 0, 150, 75);

        public async Task EnterAsync(string map, string difficulty, CancellationToken token)
        {
            var difficultyPoint = SkyTopMenuLayout.Difficulty(difficulty);
            var frame = await context.ReadAsync(Full).ConfigureAwait(false);
            if (SkyTopSceneVision.ReadRound(frame.Text) is not null || Unique(frame, text => text == "重玩关卡") is not null)
            {
                // 异常后重试的是整条刷关语句，旧关卡不能冒充新一轮，也不能在关卡里等待城镇助理。
                context.Stage("重试当前刷关语句：退出原关卡后重新进入，次数重新计数");
                await OpenSystemAsync(context, knownBattle: true).ConfigureAwait(false);
                await ConfirmSystemActionAsync(context, "退出关卡").ConfigureAwait(false);
                await WaitForTownAsync(context).ConfigureAwait(false);
                frame = await context.ReadAsync(Full).ConfigureAwait(false);
            }
            // 异常后继续可停在本关模式/难度弹窗；不要在弹窗遮挡时重新等待城镇入口。
            var difficultyOpen = Unique(frame, text => text.Contains("选择难度", StringComparison.Ordinal)) is not null;
            var modeOpen = frame.Text.Any(line => line.Bounds.X is >= 250 and <= 700 && line.Bounds.Y is >= 110 and <= 140 && Compact(line.Text) == map);
            if (!difficultyOpen)
            {
                if (!modeOpen)
                {
                    await OpenCityAsync().ConfigureAwait(false);
                    context.Stage("选择" + map + "关卡");
                    // 天空顶入口在950×600画面固定；详情框会挡住文字，直接点入口，不再等待关卡名OCR。
                    while (true)
                    {
                        await context.CaptureAsync().ConfigureAwait(false);
                        try { await context.ClickAsync(676, 444).ConfigureAwait(false); break; }
                        catch (ReobserveException) { }
                    }
                }
                await SelectNormalModeAsync().ConfigureAwait(false);
            }
            // 悬停说明会覆盖难度文字；页面切换已确认后，直接点击固定按钮中央。
            context.Stage("选择难度：" + difficulty, "选择关卡难度");
            while (true)
            {
                await context.CaptureAsync().ConfigureAwait(false);
                try { await context.ClickAsync(difficultyPoint.X, difficultyPoint.Y).ConfigureAwait(false); break; }
                catch (ReobserveException) { }
            }
            token.ThrowIfCancellationRequested();
        }

        /// <summary>后台点击回执不代表页面切换；只有原模式按钮仍在且未见难度页时，才有限重试。</summary>
        private async Task SelectNormalModeAsync()
        {
            var elapsed = Stopwatch.StartNew(); var pause = context.PausedDuration;
            var attempts = 0; var nextClick = TimeSpan.Zero;
            context.Stage("选择正常模式，等待难度页确认");
            while (elapsed.Elapsed - (context.PausedDuration - pause) < TimeSpan.FromSeconds(20))
            {
                var frame = await context.ReadAsync(new(250, 110, 450, 340)).ConfigureAwait(false);
                if (Unique(frame, text => text.Contains("选择难度", StringComparison.Ordinal)) is not null)
                { context.Trace("normal-mode-confirmed", result: "difficulty-visible"); return; }
                var buttons = await context.ReadAsync(new(260, 140, 440, 140), frame.SnapshotId).ConfigureAwait(false);
                var normal = Unique(buttons, text => Regex.IsMatch(text, @"^正常(?:[0-9]+级)?$", RegexOptions.CultureInvariant));
                var now = elapsed.Elapsed - (context.PausedDuration - pause);
                if (normal is not null && now >= nextClick && attempts < 3)
                {
                    context.Trace("normal-mode-attempt", attempt: attempts + 1);
                    try { await context.ClickAsync(normal.Bounds.X + normal.Bounds.Width / 2, normal.Bounds.Y + normal.Bounds.Height / 2).ConfigureAwait(false); }
                    catch (ReobserveException) { continue; }
                    attempts++; nextClick = now + TimeSpan.FromSeconds(1);
                }
                await Task.Delay(250, context.Token).ConfigureAwait(false);
            }
            context.Trace("normal-mode-timeout", duration: elapsed.ElapsedMilliseconds);
            throw new TimeoutException("正常模式未确认切换到难度页，已保留窗口；可查看脚本运行日志。");
        }

        /// <summary>刷材料只处理自己的地图入口；日常系统页和侧栏由上一条日常语句收尾。</summary>
        private async Task OpenCityAsync()
        {
            var elapsed = Stopwatch.StartNew(); var pause = context.PausedDuration;
            context.Stage("等待城镇地图入口");
            while (elapsed.Elapsed - (context.PausedDuration - pause) < TimeSpan.FromSeconds(120))
            {
                var frame = await context.ReadAsync(Full).ConfigureAwait(false);
                if (HasSystemMenu(frame) || HasOfficialSidebar(frame))
                    throw new InvalidOperationException("城镇被系统面板或官方脚本栏遮挡，请先完成日常收尾或手动收起后继续。");
                if (Unique(frame, text => text == "助理") is not null)
                {
                    var map = await context.ReadAsync(new(700, 250, 245, 300), frame.SnapshotId).ConfigureAwait(false);
                    var city = Unique(map, text => text == "寒光市");
                    if (city is not null)
                    {
                        context.Stage("打开寒光市地图");
                        try { await context.ClickAsync(city.Bounds.X + city.Bounds.Width / 2, city.Bounds.Y + city.Bounds.Height / 2).ConfigureAwait(false); return; }
                        catch (ReobserveException) { continue; }
                    }
                }
                await Task.Delay(250, context.Token).ConfigureAwait(false);
            }
            throw new TimeoutException("未找到寒光市入口，已保留窗口。");
        }

        public async Task<DailyMaterialRoundResult> FightAndCollectAsync(bool checkQuotaWhenEmpty, CancellationToken token)
        {
            await EnsureBattleAiAsync(true).ConfigureAwait(false);
            var elapsed = Stopwatch.StartNew(); var pause = context.PausedDuration;
            while (elapsed.Elapsed - (context.PausedDuration - pause) < TimeSpan.FromMinutes(15))
            {
                var frame = await context.ReadAsync(Hud).ConfigureAwait(false);
                var round = SkyTopSceneVision.ReadRound(frame.Text);
                var reward = await context.ReadAsync(SkyTopSceneVision.RewardOverlay, frame.SnapshotId).ConfigureAwait(false);
                var banner = SkyTopSceneVision.HasClearBanner(reward.Text);
                var pixels = await ReadPixelsAsync(frame).ConfigureAwait(false);
                var arrow = SkyTopSceneVision.FindExitArrow(pixels);
                // 重玩确认后直接识别新截图；快速通关可能跳过初始波数，两轮计时也可能相同。
                context.Stage(round is null ? "观察自动战斗及通关提示" : $"自动战斗 {round.Finished}/{round.Total}");
                if ((banner || round is { } value && value.Finished == value.Total && arrow is not null)
                    && !SkyTopSceneVision.HasBlockingPanel(pixels))
                {
                    if (hasPresident)
                    {
                        // 游戏为本账号所有存档自动拾取和拆解；等待真实时间1秒，不关闭AI或发送走位。
                        context.Stage("有总统：等待约1秒自动拾取");
                        await Task.Delay(1000, token).ConfigureAwait(false);
                        await context.CaptureAsync().ConfigureAwait(false);
                        context.Trace("president-auto-pickup");
                        if (checkQuotaWhenEmpty)
                        {
                            context.Stage($"有总统：第{DailyMaterialFarming.FirstQuotaCheckRound}轮起检查两项每日限额");
                            var quota = await ReadQuotaAsync(token).ConfigureAwait(false);
                            if (quota?.IsFull == true) return DailyMaterialRoundResult.QuotaFull;
                            // 保留已打开的系统页供重玩复用；读取失败也不能借自动拾取后的空屏推断已满。
                        }
                        return DailyMaterialRoundResult.Collected;
                    }
                    context.Stage("通关已确认，整理掉落清单");
                    if (await TidyInventoryIfFullAsync(context).ConfigureAwait(false))
                    {
                        pixels = (await context.CaptureAsync().ConfigureAwait(false)).Pixels;
                        arrow = SkyTopSceneVision.FindExitArrow(pixels);
                    }
                    var initialDrops = SkyTopDropVision.Find(pixels, SkyTopSceneVision.FindPlayer(pixels), arrow, drops);
                    context.Trace("initial-drop-check", result: initialDrops.Any(item => item.Required) ? "materials-visible" : "no-materials");
                    // 第10轮起仅首屏无材料才查；有材料先拾取，绝不在拾取结束后追加限额检查。
                    var quotaUnavailable = false;
                    if (checkQuotaWhenEmpty && !initialDrops.Any(item => item.Required))
                    {
                        context.Stage("本轮首屏未见材料，查看两项每日限额");
                        var quota = await ReadQuotaAsync(token).ConfigureAwait(false);
                        if (quota?.IsFull == true) return DailyMaterialRoundResult.QuotaFull;
                        quotaUnavailable = quota is null;
                        await DismissSystemAsync(context).ConfigureAwait(false);
                    }
                    var sawMaterial = await CollectAsync(token).ConfigureAwait(false);
                    await EnsureBattleAiAsync(true).ConfigureAwait(false);
                    if (quotaUnavailable && !sawMaterial) context.Trace("quota-unconfirmed-empty");
                    return quotaUnavailable && !sawMaterial ? DailyMaterialRoundResult.UnconfirmedEmpty : DailyMaterialRoundResult.Collected;
                }
                await Task.Delay(500, token).ConfigureAwait(false);
            }
            throw new TimeoutException("等待自动战斗结束超时，窗口保留。");
        }

        /// <summary>N是切换开关：按前读状态，发出一次后只观察结果，避免延迟或恢复时反向切回。</summary>
        private async Task<int> EnsureBattleAiAsync(bool enabled, int riseMilliseconds = 0)
        {
            var elapsed = Stopwatch.StartNew(); var pause = context.PausedDuration; var sent = false; var used = 0;
            while (elapsed.Elapsed - (context.PausedDuration - pause) < TimeSpan.FromSeconds(10))
            {
                var scene = await context.CaptureAsync().ConfigureAwait(false);
                var state = SkyTopSceneVision.ReadBattleAi(scene.Pixels);
                if (state == enabled) { context.Trace("battle-ai-confirmed", result: enabled ? "on" : "off"); return used; }
                context.Stage(enabled ? "确认开启自动战斗" : "通关后关闭自动战斗，固定镜头");
                if (state.HasValue && !sent)
                {
                    try
                    {
                        if (!enabled && riseMilliseconds > 0)
                        {
                            await context.ToggleBattleAiAndRiseAsync(riseMilliseconds).ConfigureAwait(false);
                            used = riseMilliseconds;
                        }
                        else await context.ToggleBattleAiAsync().ConfigureAwait(false);
                        sent = true;
                    }
                    catch (ReobserveException) { continue; }
                }
                await Task.Delay(150, context.Token).ConfigureAwait(false);
            }
            throw new InvalidOperationException("无法确认自动战斗开关状态，已暂停并保留窗口，不重复切换。");
        }

        /// <summary>先上后右搜索掉落区，途中见到目标即转入当前可见区域的拾取，不强行跑完整条路线。</summary>
        private async Task<bool> CollectAsync(CancellationToken token)
        {
            var settings = drops?.Sweep ?? throw new InvalidOperationException("缺少天空顶固定扫取参数，请更新脚本资源。");
            ScriptRect? rememberedArea = null, rememberedExit = null;
            var sawMaterial = false;
            context.Trace("fixed-sweep-start", attempt: 1);
            var firstRise = await EnsureBattleAiAsync(false, Math.Min(settings.MaxRiseMilliseconds,
                Math.Max(50, settings.PulseMilliseconds))).ConfigureAwait(false);
            var interrupted = await ReachEdgeAsync('W', settings.MaxRiseMilliseconds, firstRise).ConfigureAwait(false);
            if (!interrupted) interrupted = await ReachEdgeAsync('D', settings.MaxRightMilliseconds, 0).ConfigureAwait(false);
            if ((await RemainingAsync(1).ConfigureAwait(false)).Length == 0 && rememberedArea is null)
            {
                context.Stage("当前未见必需材料，结束本轮拾取"); return sawMaterial;
            }
            // 搜索时已记录材料相对出口的位置；一帧漏识别不能抹掉尚未经过的目标区。
            context.Stage(interrupted ? "固定路线发现目标，直接拾取可见材料" : "上沿路线后仍见材料，准备补捡可见区域");
            var speedY = await MeasureAsync(horizontal: false).ConfigureAwait(false);
            var speedX = await MeasureAsync(horizontal: true).ConfigureAwait(false);
            context.Trace("fixed-sweep-calibrated", result: FormattableString.Invariant($"x={speedX:F3};y={speedY:F3}"));
            // 每次只处理新截图中的当前区域，最多三次；重叠物品新显露时继续补捡，不重启整条搜索路线。
            for (var pass = 2; pass <= 4; pass++)
            {
                var (observed, hero, exit) = await GeometryAsync().ConfigureAwait(false);
                var visible = FindVisible(observed); Remember(visible, exit);
                var area = SkyTopFixedSweep.PickupArea(visible) ?? (rememberedArea is { } old && rememberedExit is { } anchor
                    ? Shift(old, exit.X + exit.Width / 2 - anchor.X - anchor.Width / 2,
                        exit.Y + exit.Height / 2 - anchor.Y - anchor.Height / 2) : null);
                if (area is null) return sawMaterial;
                rememberedArea = area; rememberedExit = exit;
                var supplement = SkyTopFixedSweep.Build(hero, settings, speedX, speedY, area.Value);
                context.Trace("pickup-area", area, attempt: pass, duration: supplement.Sum(step => (long)step.Milliseconds));
                if (supplement.Sum(step => step.Milliseconds) > 12000)
                    throw new InvalidOperationException("可见材料补捡路线超出本关范围，已保留窗口，请检查移动校准。");
                await FollowAsync(supplement, pass).ConfigureAwait(false);
                // 每次局部拾取只做右、下、左，不再额外下移或扫第二行；收尾仍复查新显露材料。
                if ((await RemainingAsync(pass).ConfigureAwait(false)).Length == 0)
                {
                    context.Stage("目标区域已扫取，连续复查未见必需材料，拾取完成");
                    context.Trace("pickup-complete", attempt: pass, result: "swept-and-empty"); return sawMaterial;
                }
                // 拾取中也可能装满，先释放容量再走下一次有限补捡；不把满包未捡当作完成。
                await TidyInventoryIfFullAsync(context).ConfigureAwait(false);
                context.Stage("仍有材料显露，继续补捡当前区域");
            }
            throw new InvalidOperationException("三次补捡后仍见材料，已保留窗口，请检查背包或拾取情况。");

            async Task<bool> ReachEdgeAsync(char key, int limit, int used)
            {
                ScriptRect? before = null, oldExit = null;
                var lastMaterialCheck = -360;
                while (true)
                {
                    var frame = await context.CaptureAsync().ConfigureAwait(false);
                    if (SkyTopSceneVision.HasBlockingPanel(frame.Pixels)) throw new InvalidOperationException("游戏面板遮挡边界检测，已保留窗口。");
                    var miniPlayer = settings.MiniMap is { } map ? SkyTopMiniMap.FindPlayer(frame.Pixels, map) : null;
                    // 小地图是场景位置，不受抽奖遮挡和镜头跟随影响；有明确黄点时以它为准。
                    bool? miniBoundary = miniPlayer is { } dot && settings.MiniMap is { } bounds
                        ? key == 'W' ? dot.Y <= bounds.Y + 2 : dot.X >= bounds.X + bounds.Width - 3 : null;
                    var hero = SkyTopSceneVision.FindTopEdgePlayer(frame.Pixels); var exit = SkyTopSceneVision.FindExitArrow(frame.Pixels);
                    var reached = miniBoundary == true || miniBoundary is null && used > 0 && SkyTopFixedSweep.AtBoundary(before, oldExit, hero, exit, key == 'D');
                    // 按累计移动约每360ms分类一次；首帧和到边时也检查，避免每次短按都做全图匹配。
                    if (used - lastMaterialCheck >= 360 || reached || used >= limit)
                    {
                        lastMaterialCheck = used;
                        var found = FindRequired(frame);
                        if (found.Length > 0)
                        {
                            var player = SkyTopSceneVision.FindPlayer(frame.Pixels);
                            // 人物被抽奖遮住时继续既定上/右搜索，不再靠盲目下移显露人物。
                            if (player is not null && exit is { } visibleExit)
                            {
                                Remember(FindVisible(frame), visibleExit);
                                context.Trace("fixed-route-target", duration: used, textCount: found.Length, result: key.ToString());
                                return true;
                            }
                        }
                    }
                    if (reached)
                    {
                        context.Trace("boundary-confirmed", miniPlayer is { } point ? new(point.X, point.Y, 1, 1) : hero,
                            duration: used, result: key + (miniBoundary == true ? ":mini-yellow" : ":health"));
                        if (key == 'W') context.Trace("fixed-route-visible", textCount: 0, result: "W");
                        return false;
                    }
                    if (used >= limit)
                    {
                        context.Trace("boundary-input-limit", duration: used, result: key.ToString());
                        context.Stage($"{(key == 'W' ? "上移" : "右移")}达到按键上限，按已到达继续");
                        return false;
                    }
                    context.Stage(key == 'W' ? "向上到顶：检测上边界" : "沿顶向右：检测右边界", "固定路线扫取");
                    var milliseconds = Math.Min(settings.PulseMilliseconds, limit - used);
                    try { await context.PulseAsync(key, milliseconds).ConfigureAwait(false); }
                    catch (ReobserveException) { continue; }
                    // 只在松键回执后记录位移起点；恢复或拒绝的输入不消耗移动预算。
                    before = hero; oldExit = exit; used += milliseconds;
                    await Task.Delay(40, token).ConfigureAwait(false);
                }
            }

            async Task FollowAsync(IReadOnlyList<SkyTopFixedSweep.Pulse> route, int pass)
            {
                context.Trace("local-pickup-start", attempt: pass, textCount: route.Count,
                    duration: route.Sum(step => (long)step.Milliseconds));
                for (var index = 0; index < route.Count;)
                {
                    var frame = await context.CaptureAsync().ConfigureAwait(false);
                    if (SkyTopSceneVision.HasBlockingPanel(frame.Pixels)) throw new InvalidOperationException("游戏面板遮挡固定扫取，已保留窗口。");
                    var step = route[index];
                    context.Stage($"拾取可见材料 {index + 1}/{route.Count}", "局部材料拾取");
                    try { await context.PulseAsync(step.Key, step.Milliseconds, step.Second).ConfigureAwait(false); index++; }
                    catch (ReobserveException) { continue; }
                    await Task.Delay(60, token).ConfigureAwait(false);
                }
            }
            async Task<SkyTopDropVision.Drop[]> RemainingAsync(int pass)
            {
                // 空屏补一帧确认，非空立即返回；一帧动画/天气漏识别不代表材料消失。
                for (var sample = 0; sample < 2; sample++)
                {
                    await Task.Delay(120, token).ConfigureAwait(false);
                    var review = await context.CaptureAsync().ConfigureAwait(false);
                    var visible = FindVisible(review); var found = visible.Where(item => item.Required).ToArray();
                    if (found.Length > 0 && SkyTopSceneVision.FindExitArrow(review.Pixels) is { } exit) Remember(visible, exit);
                    if (found.Length > 0 || sample == 1)
                    {
                        context.Trace("fixed-sweep-review", attempt: pass, textCount: found.Length,
                            result: found.Length > 0 ? "remaining" : "two-empty-frames");
                        // 只记录固定种类、置信度和几何，不写资源包中的任意名称，便于直接定位后续误拾取。
                        foreach (var item in found)
                            context.Trace("pickup-target", item.Bounds, attempt: pass,
                                result: FormattableString.Invariant($"{item.Kind}:{item.Confidence:F3}"));
                        return found;
                    }
                }
                return [];
            }
            void Remember(IReadOnlyList<SkyTopDropVision.Drop> visible, ScriptRect exit)
            {
                if (SkyTopFixedSweep.PickupArea(visible) is not { } area) return;
                rememberedArea = area; rememberedExit = exit;
            }
            static ScriptRect Shift(ScriptRect area, int dx, int dy) => area with { X = area.X + dx, Y = area.Y + dy };
            SkyTopDropVision.Drop[] FindRequired(ScriptSceneFrame scene)
                => FindVisible(scene).Where(item => item.Required).ToArray();
            IReadOnlyList<SkyTopDropVision.Drop> FindVisible(ScriptSceneFrame scene)
            {
                var visible = SkyTopDropVision.Find(scene.Pixels, SkyTopSceneVision.FindPlayer(scene.Pixels),
                    SkyTopSceneVision.FindExitArrow(scene.Pixels), drops);
                sawMaterial |= visible.Any(item => item.Required);
                return visible;
            }

            async Task<double> MeasureAsync(bool horizontal)
            {
                while (true)
                {
                    var (_, before, oldExit) = await GeometryAsync().ConfigureAwait(false);
                    // 早停可发生在任意出生点，不能沿用“已在右上角”的S/A校准；短按选择远离最近边界的一侧。
                    var key = horizontal ? before.X < 475 || before.Y < 110 && before.X < 800 ? 'D' : 'A'
                        : before.Y < 250 ? 'S' : 'W';
                    context.Trace("pickup-calibration", before, duration: 50, result: key.ToString());
                    try { await context.PulseAsync(key, 50).ConfigureAwait(false); }
                    catch (ReobserveException) { continue; }
                    await Task.Delay(80, token).ConfigureAwait(false);
                    var (_, after, newExit) = await GeometryAsync().ConfigureAwait(false);
                    return SkyTopFixedSweep.Speed(before, oldExit, after, newExit, horizontal, 50);
                }
            }
            async Task<(ScriptSceneFrame Frame, ScriptRect Hero, ScriptRect Exit)> GeometryAsync()
            {
                var elapsed = Stopwatch.StartNew(); var pause = context.PausedDuration;
                while (elapsed.Elapsed - (context.PausedDuration - pause) < TimeSpan.FromSeconds(8))
                {
                    var scene = await context.CaptureAsync().ConfigureAwait(false);
                    if (SkyTopSceneVision.HasBlockingPanel(scene.Pixels)) throw new InvalidOperationException("游戏面板遮挡战场，已保留窗口。");
                    var player = SkyTopSceneVision.FindPlayer(scene.Pixels); var arrow = SkyTopSceneVision.FindExitArrow(scene.Pixels);
                    if (player is { } found && arrow is { } anchor) return (scene, found, anchor);
                    await Task.Delay(150, token).ConfigureAwait(false);
                }
                throw new InvalidOperationException("固定扫取未能定位人物和出口，已暂停并保留窗口。");
            }
        }

        private async Task<DailyMaterialQuota?> ReadQuotaAsync(CancellationToken token)
        {
            await OpenSystemAsync(context, knownBattle: true).ConfigureAwait(false);
            for (var attempt = 1; attempt <= 4; attempt++)
            {
                // 真实鼠标离开时游戏可能收起弹层；每次采样前重新发送后台悬停，不移动系统光标。
                // 掉落查看在标准画面位置固定；先刷新截图，再悬停按钮本体，不等待小字OCR。
                await context.CaptureAsync().ConfigureAwait(false);
                try { await context.HoverAsync(160, 488).ConfigureAwait(false); }
                catch (ReobserveException) { continue; }
                await Task.Delay(80, token).ConfigureAwait(false);
                // 实机悬停弹层向画面底部展开，最后的冥王图鉴行接近600px，不能沿用菜单本体ROI。
                var frame = await context.ReadAsync(new(0, 0, 600, 600)).ConfigureAwait(false);
                var quota = await ReadBattleQuotaAsync(context, vision, frame).ConfigureAwait(false);
                context.Trace("quota-read", attempt: attempt, result: quota is null ? "unavailable" : quota.IsFull ? "full" : "not-full");
                if (quota is null) { await Task.Delay(150, token).ConfigureAwait(false); continue; }
                context.Stage($"宝石 {quota.Gems.Collected}/{quota.Gems.Limit}；冥王图鉴 {quota.Codex.Collected}/{quota.Codex.Limit}");
                return quota;
            }
            context.Stage(hasPresident ? "限额暂未读出，继续自动拾取刷关，不以空屏推断已满"
                : "限额暂未读出，继续搜索材料，不以空屏推断已满");
            return null;
        }

        public async Task ReplayAsync(CancellationToken token)
        {
            await OpenSystemAsync(context, knownBattle: true).ConfigureAwait(false);
            await ConfirmSystemActionAsync(context, "重玩关卡").ConfigureAwait(false);
            context.Trace("replay-confirmed");
            context.Stage("观察自动战斗及通关提示");
            token.ThrowIfCancellationRequested();
        }

        public async Task ExitAsync(CancellationToken token)
        {
            await OpenSystemAsync(context, knownBattle: true).ConfigureAwait(false);
            await ConfirmSystemActionAsync(context, "退出关卡").ConfigureAwait(false);
            await WaitForTownAsync(context).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }

        private Task<ScriptPixels> ReadPixelsAsync(ScriptVisionFrame frame)
            => vision.PixelsAsync(context.Target!, Full, frame.SnapshotId, context.Token);
    }
}
