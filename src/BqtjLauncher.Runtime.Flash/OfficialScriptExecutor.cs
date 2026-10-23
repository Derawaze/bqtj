using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using BqtjLauncher.Application;
using BqtjLauncher.Domain;

namespace BqtjLauncher.Runtime.Flash;

internal sealed record OcrLine(string Text, Rectangle Bounds, double Confidence);

/// <summary>解释受限JSON视觉步骤并运行官方日常；外部不补点保存，切档只在完成确认后执行。</summary>
public sealed partial class OfficialScriptExecutor(IScriptVision vision, ScriptRunControl? control = null,
    Action<ScriptTraceEvent>? trace = null) : IScriptExecutor
{
    public IScriptExecutor ForRun(ScriptRunControl control) => new OfficialScriptExecutor(vision, control, trace);
    private readonly Guid _run = Guid.NewGuid();
    private IScriptVision Vision => vision;
    private readonly ConcurrentDictionary<Guid, AutomationWindowTarget> _targets = new();
    private static readonly ScriptRect SaveTitle = new(240, 135, 480, 65);
    private static readonly ScriptRect Sidebar = new(759, 154, 182, 420);
    // 系统菜单右下“退出存档”从x=764开始，必须覆盖完整菜单，不能沿用仅含中部按钮的区域。
    private static readonly ScriptRect SystemMenuRegion = new(90, 20, 760, 510);
    public Task BeginAsync(CancellationToken token) => vision.BeginAsync(token);
    public Task EndAsync() => vision.EndAsync();

    public async Task<bool> IsInSaveAsync(AutomationGameSession session, CancellationToken token)
    {
        var frame = await Create(session, (_, _) => { }, token).ReadAsync(new(0, 0, 950, 100)).ConfigureAwait(false);
        return Unique(frame, text => text == "助理") is not null || HasBattleHud(frame)
            || frame.Text.Any(line => Compact(line.Text) == "系统");
    }

    /// <summary>接管窗口只借助关卡HUD判断当前场景，不依赖刷材料的识图或执行模块。</summary>
    private static bool HasBattleHud(ScriptVisionFrame frame)
    {
        var text = string.Join(" ", frame.Text.Where(line => line.Bounds.X >= 800 && line.Bounds.Y < 75)
            .OrderBy(line => line.Bounds.X).Select(line => line.Text));
        var match = System.Text.RegularExpressions.Regex.Match(text, @"(?<!\d)(\d{1,2})\s*/\s*(\d{1,2}?)\s*\d{2}\s*[:：][0-5]\d(?!\d)");
        return match.Success && int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) > 0
            && int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
                <= int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<SaveSlot>> ReadSavesAsync(AutomationGameSession session, Action<string> stage, CancellationToken token)
    {
        var context = Create(session, (value, _) => stage(value), token);
        context.Stage("读取存档名称");
        var (frame, points) = await WaitSelectionLayoutAsync(context).ConfigureAwait(false);
        var slots = new List<SaveSlot>();
        for (var number = 1; number <= 8; number++)
        {
            var point = points[number - 1];
            var region = SaveSlotNameReader.NameRegion(point);
            var names = await context.ReadAsync(region, frame.SnapshotId, ScriptTextStyle.SaveName).ConfigureAwait(false);
            var name = SaveSlotNameReader.Parse(names.Text);
            slots.Add(new(number, name, names.Text.Count == 0 || names.Text.Any(line => line.Confidence < 0.85) || name.StartsWith("未识别名称", StringComparison.Ordinal)));
        }
        return slots;
    }

    public async Task ExecuteAsync(AutomationGameSession session, ScriptDefinition definition, ScriptPlanTask task,
        SpeedMultiplier speed, Action<string, ScriptRunState> stage, CancellationToken token)
    {
        var context = Create(session, stage, token);
        var start = definition.Flow.FindIndex(step => step.Id == definition.Entry);
        if (start < 0) throw new InvalidOperationException("脚本入口不存在。");
        foreach (var step in definition.Flow.Skip(start))
        {
            token.ThrowIfCancellationRequested();
            context.Stage(step.Type == "officialDaily" ? "进入存档并运行官方日常" : step.Id, step.Type);
            var region = new ScriptRect(step.Roi[0], step.Roi[1], step.Roi[2], step.Roi[3]);
            switch (step.Type)
            {
                case "officialDaily":
                    await EnterSaveAsync(context, task.SaveNumber).ConfigureAwait(false);
                    await StartOfficialAsync(context, step.Text).ConfigureAwait(false);
                    await WaitCompletionAsync(context).ConfigureAwait(false);
                    break;
                case "runOfficialDaily":
                    await StartOfficialAsync(context, step.Text).ConfigureAwait(false);
                    await WaitCompletionAsync(context).ConfigureAwait(false);
                    break;
                case "enterSave": await EnterSaveAsync(context, task.SaveNumber).ConfigureAwait(false); break;
                case "waitText": await context.WaitTextAsync(region, text => text == Compact(step.Text), step.TimeoutSeconds).ConfigureAwait(false); break;
                case "clickText":
                    await context.ClickTextAsync(region, text => text == Compact(step.Text), step.OffsetX, step.OffsetY, step.TimeoutSeconds).ConfigureAwait(false);
                    break;
                case "waitImage":
                case "clickImage":
                    if (!definition.Images.TryGetValue(step.Image, out var image)) throw new InvalidDataException("本次运行的图片快照缺失。");
                    var elapsed = Stopwatch.StartNew();
                    var pauseAtStart = context.PausedDuration;
                    var matched = false;
                    while (elapsed.Elapsed - (context.PausedDuration - pauseAtStart) < TimeSpan.FromSeconds(step.TimeoutSeconds))
                    {
                        await context.ReadAsync(region).ConfigureAwait(false);
                        ScriptRect? match;
                        try { match = await vision.FindImageAsync(context.Target!, region, image, token).ConfigureAwait(false); }
                        catch (ScriptTargetUnavailableException) { continue; }
                        if (match is null) { await Task.Delay(500, token).ConfigureAwait(false); continue; }
                        if (step.Type == "clickImage")
                        {
                            try
                            {
                                await context.ClickAsync(match.Value.X + match.Value.Width / 2 + step.OffsetX,
                                match.Value.Y + match.Value.Height / 2 + step.OffsetY).ConfigureAwait(false);
                            }
                            catch (ReobserveException) { continue; }
                        }
                        matched = true; break;
                    }
                    if (!matched) throw new TimeoutException("未找到脚本指定图片。");
                    break;
                case "click":
                    while (true)
                    {
                        await context.ReadAsync(region).ConfigureAwait(false);
                        try { await context.ClickAsync(step.X, step.Y).ConfigureAwait(false); break; }
                        catch (ReobserveException) { /* 原生端尚未发出输入，恢复画面后重建本步骤观察。 */ }
                    }
                    break;
                case "wait": await Task.Delay(step.Milliseconds, token).ConfigureAwait(false); break;
                case "waitOfficialCompletion": await WaitCompletionAsync(context).ConfigureAwait(false); break;
                default: throw new InvalidDataException("脚本步骤类型不受支持。");
            }
        }
        _ = speed; // 倍率由队列按会话临时设置，视觉执行器不写用户偏好。
    }

    private Context Create(AutomationGameSession session, Action<string, ScriptRunState> stage, CancellationToken token)
        => new(session, vision, stage, target => _targets[session.Id] = target, control, _run, trace, token);

    /// <summary>日常保存会留下系统界面；仅识别到该菜单时关闭一次，再确认面板消失。</summary>
    public Task DismissSystemAsync(AutomationGameSession session, Action<string, ScriptRunState> stage, CancellationToken token)
        => DismissSystemAsync(Create(session, stage, token));

    /// <summary>日常完成后的同档交接；持续检查过渡帧，收起系统及官方侧栏后才交给下一条语句。</summary>
    public async Task DismissDailyPanelsAsync(AutomationGameSession session, Action<string, ScriptRunState> stage, CancellationToken token)
    {
        var context = Create(session, stage, token);
        var elapsed = Stopwatch.StartNew(); var pause = context.PausedDuration; var collapseSent = false;
        context.Stage("日常收尾：关闭系统面板并收起官方侧栏");
        while (elapsed.Elapsed - (context.PausedDuration - pause) < TimeSpan.FromSeconds(120))
        {
            var frame = await context.ReadAsync(new(0, 0, 950, 600)).ConfigureAwait(false);
            if (HasSystemMenu(frame)) { await DismissSystemAsync(context).ConfigureAwait(false); continue; }
            if (HasOfficialSidebar(frame))
            {
                if (!collapseSent)
                {
                    try { await context.ClickAsync(937, 178).ConfigureAwait(false); collapseSent = true; }
                    catch (ReobserveException) { continue; }
                }
            }
            else if (Unique(frame, text => text == "助理") is not null) return;
            await Task.Delay(250, token).ConfigureAwait(false);
        }
        throw new TimeoutException("日常收尾未能确认系统和官方侧栏已收起，已保留窗口。");
    }

    private static bool HasOfficialSidebar(ScriptVisionFrame frame)
        => frame.Text.Any(line => line.Bounds.X >= Sidebar.X && line.Bounds.Y is >= 154 and <= 190
            && System.Text.RegularExpressions.Regex.IsMatch(Compact(line.Text), @"[0-9]+/[0-9]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant));

    private static async Task DismissSystemAsync(Context context)
    {
        var frame = await context.ReadAsync(SystemMenuRegion).ConfigureAwait(false);
        if (!HasSystemMenu(frame)) return;
        context.Stage("收起系统面板，继续后续语句");
        while (true)
        {
            try { await context.ClickAsync(825, 44).ConfigureAwait(false); break; }
            catch (ReobserveException)
            {
                frame = await context.ReadAsync(SystemMenuRegion).ConfigureAwait(false);
                if (!HasSystemMenu(frame)) return;
            }
        }
        var elapsed = Stopwatch.StartNew(); var pause = context.PausedDuration;
        while (elapsed.Elapsed - (context.PausedDuration - pause) < TimeSpan.FromSeconds(10))
        {
            frame = await context.ReadAsync(SystemMenuRegion).ConfigureAwait(false);
            if (!HasSystemMenu(frame)) return;
            await Task.Delay(250, context.Token).ConfigureAwait(false);
        }
        throw new InvalidOperationException("系统面板未关闭，已保留窗口。");
    }

    /// <summary>完成侧栏可能挡住退出按钮；标题和保存按钮共同出现也能确认系统面板。</summary>
    private static bool HasSystemMenu(ScriptVisionFrame frame)
    {
        var labels = frame.Text.Select(line => Compact(line.Text)).ToHashSet(StringComparer.Ordinal);
        return labels.Contains("退出存档") || labels.Contains("重玩关卡") || labels.Contains("系统") && labels.Contains("保存存档");
    }

    /// <summary>识别存档弹窗后校验完整两列四行，再点击目标卡片；不按桌面坐标选档。</summary>
    private async Task EnterSaveAsync(Context context, int number)
    {
        context.Stage("选择存档" + number);
        while (true)
        {
            var (_, points) = await WaitSelectionLayoutAsync(context).ConfigureAwait(false);
            var point = points[number - 1];
            try { await context.ClickAsync(point.X, point.Y).ConfigureAwait(false); }
            catch (ReobserveException) { continue; }
            await context.WaitTextAsync(new(0, 0, 950, 100), text => text == "助理").ConfigureAwait(false);
            return;
        }
    }

    /// <summary>等完整菜单绘制后才读名称或选档；重试只截图，不重复点击首页的读取按钮。</summary>
    private async Task<(ScriptVisionFrame, Point[])> WaitSelectionLayoutAsync(Context context)
    {
        var (frame, title) = await OpenSelectionAsync(context).ConfigureAwait(false);
        var elapsed = Stopwatch.StartNew();
        var pauseAtStart = context.PausedDuration;
        while (elapsed.Elapsed - (context.PausedDuration - pauseAtStart) < TimeSpan.FromSeconds(15))
        {
            using var bitmap = ToBitmap(await vision.PixelsAsync(context.Target!, new(0, 0, 950, 600), frame.SnapshotId, context.Token).ConfigureAwait(false));
            if (title is not null && DailyMenuGeometry.TryFindSaveSlot(bitmap, ToRectangle(title.Bounds), 1, out _))
                return (frame, Enumerable.Range(1, 8).Select(number => DailyMenuGeometry.FindSaveSlot(bitmap, ToRectangle(title.Bounds), number)).ToArray());
            context.Stage("等待存档卡片完整显示");
            await Task.Delay(250, context.Token).ConfigureAwait(false);
            frame = await context.ReadAsync(SaveTitle).ConfigureAwait(false);
            title = Unique(frame, DailyMenuGeometry.IsSaveDialogTitle);
        }
        throw new InvalidOperationException("存档卡片布局持续未匹配，请保留窗口检查选档画面。");
    }

    private static async Task<(ScriptVisionFrame, ScriptText)> OpenSelectionAsync(Context context)
    {
        var frame = await context.ReadAsync(SaveTitle).ConfigureAwait(false);
        var title = Unique(frame, text => text == "读取存档");
        if (title is not null) return (frame, title);
        await context.ClickTextAsync(new(350, 530, 270, 70), text => text == "读取存档").ConfigureAwait(false);
        return await context.WaitTextAsync(SaveTitle, text => text == "读取存档").ConfigureAwait(false);
    }

    private static async Task StartOfficialAsync(Context context, string requestedName)
    {
        await DismissSystemAsync(context).ConfigureAwait(false);
        context.Stage("打开助理，选择官方日常");
        await context.ClickTextAsync(new(0, 0, 950, 100), text => text == "助理", 0, -20).ConfigureAwait(false);
        var names = string.IsNullOrWhiteSpace(requestedName) ? new[] { "日常操作复制", "日常操作" } : [Compact(requestedName)];
        // 同一帧按优先级选择，两个默认名称同时存在时也只能点击第一顺位。
        var elapsed = Stopwatch.StartNew(); var pause = context.PausedDuration;
        while (true)
        {
            var frame = await context.ReadAsync(new(90, 70, 210, 340)).ConfigureAwait(false);
            var selected = names.Select(name => Unique(frame, text => text == name)).FirstOrDefault(item => item is not null);
            if (selected is null)
            {
                if (elapsed.Elapsed - (context.PausedDuration - pause) > TimeSpan.FromSeconds(30))
                    throw new TimeoutException("助理列表中未找到指定的官方日常脚本，请检查脚本名称。");
                await Task.Delay(500, context.Token).ConfigureAwait(false); continue;
            }
            context.DailyName = Compact(selected.Text);
            try { await context.ClickAsync(selected.Bounds.X + selected.Bounds.Width / 2, selected.Bounds.Y + selected.Bounds.Height / 2).ConfigureAwait(false); break; }
            catch (ReobserveException) { }
        }
        context.Stage("运行游戏内脚本：" + context.DailyName, "运行官方日常");
        await context.WaitTextAsync(new(300, 100, 350, 95), text => text.Trim('《', '》', '〈', '〉') == context.DailyName).ConfigureAwait(false);
        await context.ClickTextAsync(new(480, 475, 200, 55), text => text == "运行").ConfigureAwait(false);
        context.Stage("确认运行官方日常");
        while (true)
        {
            var (prompt, _) = await context.WaitTextAsync(new(260, 170, 450, 100), text => DailyRunPrompt.IsExpected(text, context.DailyName)).ConfigureAwait(false);
            var confirm = await context.ReadAsync(new(345, 295, 100, 60), prompt.SnapshotId).ConfigureAwait(false);
            var button = Unique(confirm, text => text == "确定") ?? throw new InvalidOperationException("运行确认弹窗没有唯一确定按钮。");
            try { await context.ClickAsync(button.Bounds.X + button.Bounds.Width / 2, button.Bounds.Y + button.Bounds.Height / 2).ConfigureAwait(false); return; }
            catch (ReobserveException) { /* 输入尚未发出，只重新观察确认弹窗，不再次点击运行。 */ }
        }
    }

    /// <summary>观察本次进度增长及同帧N/N标题左侧绿勾，持续3秒才完成；冷却与保存由游戏执行。</summary>
    private async Task WaitCompletionAsync(Context context)
    {
        var epoch = Stopwatch.GetTimestamp();
        var observer = new DailyScriptRunObserver(Guid.NewGuid(), 3, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
        context.Stage("观察官方日常进度，等待自动保存");
        while (true)
        {
            var frame = await context.ReadAsync(Sidebar).ConfigureAwait(false);
            var progress = DailyScriptProgressParser.Parse(frame.Text.Select(line => line.Text), context.DailyName);
            var completed = false;
            if (progress is not null && progress.Completed == progress.Total)
            {
                // 列表可滚动且末行可能出视口；用户确认整体N/N标题左侧绿勾是官方结束标志。
                using var sidebar = ToBitmap(await vision.PixelsAsync(context.Target!, Sidebar, frame.SnapshotId, context.Token).ConfigureAwait(false));
                var lines = frame.Text.Select(line => new OcrLine(line.Text,
                    new Rectangle(line.Bounds.X - Sidebar.X, line.Bounds.Y - Sidebar.Y, line.Bounds.Width, line.Bounds.Height), line.Confidence)).ToArray();
                completed = GreenScriptVision.FindCompletedHeader(sidebar, lines, context.DailyName) is not null;
            }
            var result = observer.Observe(Stopwatch.GetElapsedTime(epoch, frame.CapturedAt), Stopwatch.GetElapsedTime(epoch),
                progress, completed, frame.Text.Any(line => line.Text.TrimStart().StartsWith('×')));
            context.Stage(progress is null ? "等待识别本次日常进度"
                : result.State == DailyScriptCompletionState.AwaitingStart
                    ? completed ? $"已识别 {progress.Completed}/{progress.Total} 及绿勾，等待本次进度变化证据"
                        : $"官方日常 {progress.Completed}/{progress.Total}，等待本次进度变化"
                    : completed ? $"官方日常 {progress.Completed}/{progress.Total}，已识别完成绿勾，连续确认中"
                        : $"官方日常 {progress.Completed}/{progress.Total}，等待标题完成绿勾");
            if (result.State == DailyScriptCompletionState.VisualComplete)
            {
                context.Stage(result.HasFailedItems ? "官方脚本已完成；部分中间步骤未成功" : "官方脚本已完成");
                return;
            }
            await Task.Delay(1000, context.Token).ConfigureAwait(false);
        }
    }

    public async Task ExitSaveAsync(AutomationGameSession session, Action<string, ScriptRunState> stage, CancellationToken token)
    {
        var context = Create(session, stage, token);
        context.Stage("退出当前存档");
        // 实机齿轮入口叫“系统”；关卡内退出存档为禁用态，必须先返回城镇再退出。
        var settings = await OpenSystemAsync(context).ConfigureAwait(false);
        if (Unique(settings, text => text == "退出关卡") is not null)
        {
            await ConfirmSystemActionAsync(context, "退出关卡").ConfigureAwait(false);
            await WaitForTownAsync(context).ConfigureAwait(false);
            await OpenSystemAsync(context).ConfigureAwait(false);
        }
        await ConfirmSystemActionAsync(context, "退出存档").ConfigureAwait(false);
        await context.WaitTextAsync(new(350, 530, 270, 70), text => text == "读取存档").ConfigureAwait(false);
    }

    /// <summary>先确认场景再点其齿轮；统一菜单复用已打开的保存/系统界面。</summary>
    private static async Task<ScriptVisionFrame> OpenSystemAsync(Context context, bool knownBattle = false)
    {
        while (true)
        {
            var frame = await context.ReadAsync(new(0, 0, 950, 600)).ConfigureAwait(false);
            if (HasSystemMenu(frame)) return frame;
            // 刷取流程刚完成本轮通关/拾取时已经确认在关卡内，不让随后一次小字OCR漏读推翻该状态。
            var battle = knownBattle || HasBattleHud(frame);
            if (!battle && Unique(frame, text => text == "助理") is null)
                throw new InvalidOperationException("未确认城镇或关卡画面，无法打开系统菜单；窗口保留。");
            try { await context.ClickAsync(battle ? 29 : 742, battle ? 574 : 565).ConfigureAwait(false); }
            catch (ReobserveException) { continue; }
            // 关卡中的“退出存档”是灰色禁用项，不能用它判断菜单已打开。
            return (await context.WaitTextAsync(SystemMenuRegion, text => text == (battle ? "退出关卡" : "退出存档")).ConfigureAwait(false)).Item1;
        }
    }

    /// <summary>各刷关语句共用系统动作；重玩点固定按钮，弹窗出现后点固定确定，已发送动作不重复发送。</summary>
    private static async Task ConfirmSystemActionAsync(Context context, string action)
    {
        if (action is "重玩关卡" or "退出关卡")
        {
            while (true)
            {
                try { await context.ClickAsync(470, action == "重玩关卡" ? 309 : 356).ConfigureAwait(false); break; }
                catch (ReobserveException) { await OpenSystemAsync(context, knownBattle: true).ConfigureAwait(false); }
            }
        }
        else await context.ClickTextAsync(SystemMenuRegion, text => text == action).ConfigureAwait(false);
        while (true)
        {
            var (frame, _) = await context.WaitTextAsync(new(200, 130, 550, 150), text => text.Contains(action, StringComparison.Ordinal)).ConfigureAwait(false);
            if (action is "重玩关卡" or "退出关卡")
            {
                try { await context.ClickAsync(420, 315).ConfigureAwait(false); return; }
                catch (ReobserveException) { continue; }
            }
            var buttons = await context.ReadAsync(new(365, 290, 110, 65), frame.SnapshotId).ConfigureAwait(false);
            var confirm = Unique(buttons, text => text == "确定") ?? throw new InvalidOperationException("确认框缺少唯一确定按钮，窗口保留。");
            try { await context.ClickAsync(confirm.Bounds.X + confirm.Bounds.Width / 2, confirm.Bounds.Y + confirm.Bounds.Height / 2).ConfigureAwait(false); return; }
            catch (ReobserveException) { /* 输入未发出，恢复后只重读当前确认框。 */ }
        }
    }

    /// <summary>退出关卡后处理胜利评价再确认城镇；只在标题和评分同时匹配时关闭已校准的右上角。</summary>
    private static async Task WaitForTownAsync(Context context)
    {
        var elapsed = Stopwatch.StartNew(); var pause = context.PausedDuration;
        var resultClosed = false; var townFrames = 0;
        while (elapsed.Elapsed - (context.PausedDuration - pause) < TimeSpan.FromSeconds(120))
        {
            var frame = await context.ReadAsync(new(0, 0, 950, 500)).ConfigureAwait(false);
            var victory = frame.Text.Any(line => Compact(line.Text) == "胜利" && line.Bounds.X is >= 410 and <= 540 && line.Bounds.Y is >= 45 and <= 90);
            var score = frame.Text.Any(line => Compact(line.Text).StartsWith("综合评分", StringComparison.Ordinal)
                && line.Bounds.X is >= 380 and <= 560 && line.Bounds.Y is >= 150 and <= 210);
            if (victory && score)
            {
                townFrames = 0;
                if (!resultClosed)
                {
                    context.Stage("关闭关卡胜利评价");
                    try { await context.ClickAsync(691, 60).ConfigureAwait(false); resultClosed = true; }
                    catch (ReobserveException) { continue; }
                }
            }
            else if (Unique(frame, text => text == "助理") is not null)
            {
                if (++townFrames >= 2) return;
            }
            else townFrames = 0;
            await Task.Delay(250, context.Token).ConfigureAwait(false);
        }
        throw new TimeoutException("退出关卡后未能确认城镇画面，已保留窗口。");
    }

    public async Task ReleaseAsync(AutomationGameSession session)
    {
        if (_targets.TryRemove(session.Id, out var target)) await vision.ReleaseTargetAsync(target).ConfigureAwait(false);
    }

    private static ScriptText? Unique(ScriptVisionFrame frame, Func<string, bool> match)
    {
        var found = frame.Text.Where(line => match(Compact(line.Text))).ToArray();
        if (found.Length > 1) throw new InvalidOperationException("识别到多个相同按钮，未发送点击。");
        return found.SingleOrDefault();
    }

    private static string Compact(string text) => string.Concat(text.Normalize(NormalizationForm.FormKC).Where(character => !char.IsWhiteSpace(character)));
    private static Rectangle ToRectangle(ScriptRect value) => new(value.X, value.Y, value.Width, value.Height);

    internal static Bitmap ToBitmap(ScriptPixels pixels)
    {
        var bitmap = new Bitmap(pixels.Width, pixels.Height, PixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(new(0, 0, pixels.Width, pixels.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (var y = 0; y < pixels.Height; y++)
                Marshal.Copy(pixels.Bgr, y * pixels.Width * 3, data.Scan0 + y * data.Stride, pixels.Width * 3);
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    /// <summary>每个存档独立上下文；不可用画面自动等待恢复，已经发送的点击不在这里重放。</summary>
    private sealed class Context(AutomationGameSession session, IScriptVision vision,
        Action<string, ScriptRunState> report, Action<AutomationWindowTarget> targetChanged, ScriptRunControl? control,
        Guid run, Action<ScriptTraceEvent>? trace, CancellationToken token)
    {
        public CancellationToken Token => token;
        public AutomationWindowTarget? Target { get; private set; }
        public string DailyName { get; set; } = "日常操作复制";
        private string _stage = "等待游戏加载";
        private long _capturedAt;
        private long _snapshot;
        private string _traceStage = "等待游戏加载";
        private string? _lastObservation;
        private long _lastObservationAt;
        private bool _unavailableReported;
        private bool _initialObservationRetried;
        private long _observedRevision;
        private TimeSpan _paused;
        public TimeSpan PausedDuration => _paused;
        public void Stage(string value, string? diagnosticStage = null)
        {
            _stage = value; report(value, ScriptRunState.Running);
            var label = diagnosticStage ?? value;
            if (_traceStage == label) return;
            _traceStage = label; Trace("stage");
        }

        /// <summary>日志不收集任意OCR文本；菜单固定词、快照和点击回执足以区分识别失败与输入未生效。</summary>
        public void Trace(string kind, ScriptRect? region = null, int? x = null, int? y = null,
            long? duration = null, string? result = null, IReadOnlyList<ScriptTraceText>? menu = null, int? textCount = null, int? attempt = null)
            => trace?.Invoke(new(DateTimeOffset.UtcNow, run, session.Id, kind, _traceStage, _snapshot,
                _capturedAt == 0 ? 0 : (long)Stopwatch.GetElapsedTime(_capturedAt).TotalMilliseconds, region, x, y, duration, result, menu, textCount, attempt));

        public async Task<ScriptVisionFrame> ReadAsync(ScriptRect region, long snapshot = 0, ScriptTextStyle style = ScriptTextStyle.Normal)
        {
            var elapsed = Stopwatch.StartNew();
            ScriptVisionFrame frame;
            try { frame = await ObserveAsync(target => vision.ReadAsync(target, region, token, snapshot, style), frame => frame.CapturedAt).ConfigureAwait(false); }
            catch (Exception error)
            {
                Trace("observe-error", region, duration: elapsed.ElapsedMilliseconds,
                    result: error.Data["ScriptVisionCode"] as string ?? error.GetType().Name);
                throw;
            }
            _snapshot = frame.SnapshotId;
            if (trace is not null)
            {
                var menu = frame.Text.Select(line => new ScriptTraceText(Compact(line.Text), line.Bounds, Math.Round(line.Confidence, 3)))
                    .Where(line => IsDiagnosticMenuText(line.Text)).ToArray();
                var fingerprint = region + ":" + string.Join("|", menu.Select(line => line.Text + line.Bounds));
                // 同一等待画面最多每5秒记一次；变化立即记录，避免按截图频率刷满日志。
                if (fingerprint != _lastObservation || Stopwatch.GetElapsedTime(_lastObservationAt) >= TimeSpan.FromSeconds(5))
                {
                    Trace("observe", region, duration: elapsed.ElapsedMilliseconds, menu: menu, textCount: frame.Text.Count);
                    _lastObservation = fingerprint; _lastObservationAt = Stopwatch.GetTimestamp();
                }
            }
            return frame;
        }

        private static bool IsDiagnosticMenuText(string text) => text is "助理" or "寒光市" or "天空顶" or "选择难度" or "系统" or "保存存档"
            or "退出存档" or "重玩关卡" or "退出关卡" or "读取存档" or "确定" or "运行" or "日常操作复制"
            or "背包" or "满" or "武器" or "装备" or "基因体" or "一键拆解" or "一键分解" or "掉落查看"
            || System.Text.RegularExpressions.Regex.IsMatch(text, @"^(?:(?:正常|挑战)(?:[0-9]+级)?|(?:简单|普通|困难|噩梦|地狱)[一二三四五六]?|[√V]?日常操作复制[0-9]+/[0-9]+)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        public async Task<ScriptSceneFrame> CaptureAsync()
        {
            var scene = await ObserveAsync(target => vision.CaptureAsync(target, token), scene => scene.Frame.CapturedAt).ConfigureAwait(false);
            _snapshot = scene.Frame.SnapshotId;
            return scene;
        }

        /// <summary>识字与纯截图共用目标绑定、暂停和新鲜度检查，避免两条输入路径的恢复行为分叉。</summary>
        private async Task<T> ObserveAsync<T>(Func<AutomationWindowTarget, Task<T>> observe, Func<T, long> capturedAt)
        {
            var unavailable = Stopwatch.StartNew();
            var reconnected = false;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (control is not null)
                {
                    var paused = await control.WaitAsync(token).ConfigureAwait(false); _paused += paused;
                    if (paused > TimeSpan.Zero) Trace("resume-observation", duration: (long)paused.TotalMilliseconds);
                }
                if (session.Completion.IsCompleted) throw new InvalidOperationException("本次游戏窗口已关闭。");
                try
                {
                    Target = await session.GetTargetAsync(token).ConfigureAwait(false);
                    targetChanged(Target);
                    _observedRevision = control?.Revision ?? 0;
                    var frame = await observe(Target).ConfigureAwait(false);
                    _capturedAt = capturedAt(frame);
                    if (reconnected) report(_stage, ScriptRunState.Running);
                    if (_unavailableReported) { Trace("target-restored"); _unavailableReported = false; }
                    return frame;
                }
                catch (ScriptTargetUnavailableException error)
                {
                    if (!_unavailableReported) { Trace("target-unavailable", result: error.GetType().Name); _unavailableReported = true; }
                    report(error.Message, ScriptRunState.Paused);
                    var paused = Stopwatch.StartNew();
                    await Task.Delay(500, token).ConfigureAwait(false);
                    _paused += paused.Elapsed;
                }
                catch (InvalidOperationException error) when (!_initialObservationRetried && _capturedAt == 0 && Target is not null
                    && error.Data["ScriptVisionCode"] is "vision" or "capture" or "controller-connect" or "recognition")
                {
                    // 首次画面尚未取得时只重建一次本窗口控制器；已有观察或点击之后不走这条恢复路径。
                    _initialObservationRetried = true; reconnected = true;
                    Trace("initial-observation-retry", result: (string)error.Data["ScriptVisionCode"]!, attempt: 1);
                    report("首次视觉观察未就绪，重新连接后重试一次", ScriptRunState.Waiting);
                    await vision.ReleaseTargetAsync(Target).ConfigureAwait(false);
                    await Task.Delay(500, token).ConfigureAwait(false);
                }
                catch (InvalidOperationException) when (Target is null && unavailable.Elapsed < TimeSpan.FromSeconds(60))
                { report("等待游戏加载或登录", ScriptRunState.Waiting); await Task.Delay(250, token).ConfigureAwait(false); }
            }
        }

        public async Task<(ScriptVisionFrame, ScriptText)> WaitTextAsync(ScriptRect region, Func<string, bool> match, int seconds = 120)
        {
            var elapsed = Stopwatch.StartNew();
            var pauseAtStart = _paused;
            Trace("wait-start", region, duration: seconds * 1000L);
            while (elapsed.Elapsed - (_paused - pauseAtStart) < TimeSpan.FromSeconds(seconds))
            {
                var frame = await ReadAsync(region).ConfigureAwait(false);
                var found = Unique(frame, match);
                if (found is not null) { Trace("wait-matched", region, duration: elapsed.ElapsedMilliseconds); return (frame, found); }
                await Task.Delay(500, token).ConfigureAwait(false);
            }
            Trace("wait-timeout", region, duration: elapsed.ElapsedMilliseconds);
            throw new TimeoutException("等待界面超时：" + _stage);
        }

        public async Task ClickTextAsync(ScriptRect region, Func<string, bool> match, int offsetX = 0, int offsetY = 0, int seconds = 120)
        {
            while (true)
            {
                var (_, text) = await WaitTextAsync(region, match, seconds).ConfigureAwait(false);
                try { await ClickAsync(text.Bounds.X + text.Bounds.Width / 2 + offsetX, text.Bounds.Y + text.Bounds.Height / 2 + offsetY).ConfigureAwait(false); return; }
                catch (ReobserveException) { /* 目标在输入前不可用，恢复后重新找按钮，不复用旧坐标。 */ }
            }
        }

        public async Task ClickAsync(int x, int y)
        {
            await RequireFreshInputAsync().ConfigureAwait(false);
            Trace("click-send", x: x, y: y);
            var elapsed = Stopwatch.StartNew();
            try { await vision.ClickAsync(Target!, x, y, token).ConfigureAwait(false); Trace("click-ack", x: x, y: y, duration: elapsed.ElapsedMilliseconds); }
            catch (ScriptTargetUnavailableException)
            {
                Trace("click-rejected", x: x, y: y);
                await ReadAsync(new(0, 0, 950, 600)).ConfigureAwait(false);
                throw new ReobserveException();
            }
            catch (Exception error) { Trace("click-error", x: x, y: y, result: error.Data["ScriptVisionCode"] as string ?? error.GetType().Name); throw; }
        }

        /// <summary>后台移动同样绑定最近一次观察；不可用时重读画面，不保留旧走位决策。</summary>
        public async Task PulseAsync(char key, int milliseconds, char? secondKey = null)
        {
            await RequireFreshInputAsync().ConfigureAwait(false);
            try
            {
                if (secondKey is { } second) await vision.HoldKeysAsync(Target!, key, second, milliseconds, token).ConfigureAwait(false);
                else await vision.HoldKeyAsync(Target!, key, milliseconds, token).ConfigureAwait(false);
            }
            catch (ScriptTargetUnavailableException) { await ReadAsync(new(0, 0, 950, 600)).ConfigureAwait(false); throw new ReobserveException(); }
        }
        public async Task HoverAsync(int x, int y)
        {
            await RequireFreshInputAsync().ConfigureAwait(false);
            Trace("hover-send", x: x, y: y);
            try { await vision.MovePointerAsync(Target!, x, y, token).ConfigureAwait(false); Trace("hover-ack", x: x, y: y); }
            catch (ScriptTargetUnavailableException) { await ReadAsync(new(0, 0, 950, 600)).ConfigureAwait(false); throw new ReobserveException(); }
        }

        /// <summary>开关输入只针对本会话；暂停恢复后先重新观察，回执不确定不重发。</summary>
        public async Task ToggleBattleAiAsync()
        {
            await RequireFreshInputAsync().ConfigureAwait(false);
            Trace("battle-ai-send");
            try { await vision.ToggleBattleAiAsync(Target!, token).ConfigureAwait(false); Trace("battle-ai-ack"); }
            catch (ScriptTargetUnavailableException) { await CaptureAsync().ConfigureAwait(false); throw new ReobserveException(); }
        }

        /// <summary>首个上移与N切换由同一原生请求完成；回执前不重发，返回后计入1.5秒总预算。</summary>
        public async Task ToggleBattleAiAndRiseAsync(int milliseconds)
        {
            await RequireFreshInputAsync().ConfigureAwait(false);
            Trace("battle-ai-rise-send", duration: milliseconds);
            try { await vision.ToggleBattleAiAndRiseAsync(Target!, milliseconds, token).ConfigureAwait(false); Trace("battle-ai-rise-ack"); }
            catch (ScriptTargetUnavailableException) { await CaptureAsync().ConfigureAwait(false); throw new ReobserveException(); }
        }
        private async Task RequireFreshInputAsync()
        {
            token.ThrowIfCancellationRequested();
            if (control is not null)
            {
                _paused += await control.WaitAsync(token).ConfigureAwait(false);
                // 暂停期间游戏可能继续运行，恢复后必须重新定位，不能点击暂停前的坐标。
                if (_observedRevision != control.Revision)
                {
                    await ReadAsync(new(0, 0, 950, 600)).ConfigureAwait(false);
                    throw new ReobserveException();
                }
            }
            if (Target is null || Stopwatch.GetElapsedTime(_capturedAt) > TimeSpan.FromSeconds(5))
                throw new InvalidOperationException("输入前截图已过期，暂停并保留窗口。");
        }
    }

    /// <summary>仅在原生端明确拒绝输入前使用；输入回执不确定绝不进入自动重试。</summary>
    private sealed class ReobserveException : Exception;
}
