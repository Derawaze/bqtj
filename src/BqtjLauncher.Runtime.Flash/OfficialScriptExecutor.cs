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
public sealed class OfficialScriptExecutor(IScriptVision vision) : IScriptExecutor
{
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
            context.Stage(step.Type == "officialDaily" ? "进入存档并运行官方日常" : step.Id);
            var region = new ScriptRect(step.Roi[0], step.Roi[1], step.Roi[2], step.Roi[3]);
            switch (step.Type)
            {
                case "officialDaily":
                    await EnterSaveAsync(context, task.SaveNumber).ConfigureAwait(false);
                    await StartOfficialAsync(context).ConfigureAwait(false);
                    await WaitCompletionAsync(context).ConfigureAwait(false);
                    break;
                case "runOfficialDaily":
                    await StartOfficialAsync(context).ConfigureAwait(false);
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
        => new(session, vision, stage, target => _targets[session.Id] = target, token);

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

    private static async Task StartOfficialAsync(Context context)
    {
        context.Stage("打开助理并选择日常操作复制");
        await context.ClickTextAsync(new(0, 0, 950, 100), text => text == "助理", 0, -20).ConfigureAwait(false);
        await context.ClickTextAsync(new(90, 70, 210, 340), text => text == "日常操作复制").ConfigureAwait(false);
        await context.WaitTextAsync(new(300, 100, 350, 95), text => text.Trim('《', '》', '〈', '〉') == "日常操作复制").ConfigureAwait(false);
        await context.ClickTextAsync(new(480, 475, 200, 55), text => text == "运行").ConfigureAwait(false);
        context.Stage("确认运行官方日常");
        while (true)
        {
            var (prompt, _) = await context.WaitTextAsync(new(260, 170, 450, 100), DailyRunPrompt.IsExpected).ConfigureAwait(false);
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
            var progress = DailyScriptProgressParser.Parse(frame.Text.Select(line => line.Text));
            var completed = false;
            if (progress is not null && progress.Completed == progress.Total)
            {
                // 列表可滚动且末行可能出视口；用户确认整体N/N标题左侧绿勾是官方结束标志。
                using var sidebar = ToBitmap(await vision.PixelsAsync(context.Target!, Sidebar, frame.SnapshotId, context.Token).ConfigureAwait(false));
                var lines = frame.Text.Select(line => new OcrLine(line.Text,
                    new Rectangle(line.Bounds.X - Sidebar.X, line.Bounds.Y - Sidebar.Y, line.Bounds.Width, line.Bounds.Height), line.Confidence)).ToArray();
                completed = GreenScriptVision.FindCompletedHeader(sidebar, lines) is not null;
            }
            var result = observer.Observe(Stopwatch.GetElapsedTime(epoch, frame.CapturedAt), Stopwatch.GetElapsedTime(epoch),
                progress, completed, frame.Text.Any(line => line.Text.TrimStart().StartsWith('×')));
            context.Stage(progress is null ? "等待识别本次日常进度" : $"官方日常 {progress.Completed}/{progress.Total}，等待标题完成绿勾");
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
            await context.WaitTextAsync(new(0, 0, 950, 100), text => text == "助理").ConfigureAwait(false);
            await OpenSystemAsync(context).ConfigureAwait(false);
        }
        await ConfirmSystemActionAsync(context, "退出存档").ConfigureAwait(false);
        await context.WaitTextAsync(new(350, 530, 270, 70), text => text == "读取存档").ConfigureAwait(false);
    }

    /// <summary>先确认场景再点其齿轮；统一菜单复用已打开的保存/系统界面。</summary>
    private static async Task<ScriptVisionFrame> OpenSystemAsync(Context context)
    {
        while (true)
        {
            var frame = await context.ReadAsync(new(0, 0, 950, 600)).ConfigureAwait(false);
            if (Unique(frame, text => text == "退出存档") is not null || Unique(frame, text => text == "重玩关卡") is not null) return frame;
            var battle = HasBattleHud(frame);
            if (!battle && Unique(frame, text => text == "助理") is null)
                throw new InvalidOperationException("未确认城镇或关卡画面，无法打开系统菜单；窗口保留。");
            try { await context.ClickAsync(battle ? 29 : 742, battle ? 574 : 565).ConfigureAwait(false); }
            catch (ReobserveException) { continue; }
            return (await context.WaitTextAsync(SystemMenuRegion, text => text == "退出存档").ConfigureAwait(false)).Item1;
        }
    }

    /// <summary>只确认与本次按钮文字一致的弹窗；已发出的重玩/退出不重复发送。</summary>
    private static async Task ConfirmSystemActionAsync(Context context, string action)
    {
        await context.ClickTextAsync(SystemMenuRegion, text => text == action).ConfigureAwait(false);
        while (true)
        {
            var (frame, _) = await context.WaitTextAsync(new(200, 130, 550, 150), text => text.Contains(action, StringComparison.Ordinal)).ConfigureAwait(false);
            var buttons = await context.ReadAsync(new(365, 290, 110, 65), frame.SnapshotId).ConfigureAwait(false);
            var confirm = Unique(buttons, text => text == "确定") ?? throw new InvalidOperationException("确认框缺少唯一确定按钮，窗口保留。");
            try { await context.ClickAsync(confirm.Bounds.X + confirm.Bounds.Width / 2, confirm.Bounds.Y + confirm.Bounds.Height / 2).ConfigureAwait(false); return; }
            catch (ReobserveException) { /* 输入未发出，恢复后只重读当前确认框。 */ }
        }
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
        Action<string, ScriptRunState> report, Action<AutomationWindowTarget> targetChanged, CancellationToken token)
    {
        public CancellationToken Token => token;
        public AutomationWindowTarget? Target { get; private set; }
        private string _stage = "等待游戏加载";
        private long _capturedAt;
        private TimeSpan _paused;
        public TimeSpan PausedDuration => _paused;
        public void Stage(string value) { _stage = value; report(value, ScriptRunState.Running); }

        public async Task<ScriptVisionFrame> ReadAsync(ScriptRect region, long snapshot = 0, ScriptTextStyle style = ScriptTextStyle.Normal)
        {
            var unavailable = Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (session.Completion.IsCompleted) throw new InvalidOperationException("本次游戏窗口已关闭。");
                try
                {
                    Target = await session.GetTargetAsync(token).ConfigureAwait(false);
                    targetChanged(Target);
                    var frame = await vision.ReadAsync(Target, region, token, snapshot, style).ConfigureAwait(false);
                    _capturedAt = frame.CapturedAt;
                    return frame;
                }
                catch (ScriptTargetUnavailableException error)
                {
                    report(error.Message, ScriptRunState.Paused);
                    var paused = Stopwatch.StartNew();
                    await Task.Delay(500, token).ConfigureAwait(false);
                    _paused += paused.Elapsed;
                }
                catch (InvalidOperationException) when (Target is null && unavailable.Elapsed < TimeSpan.FromSeconds(60))
                { report("等待游戏加载或登录", ScriptRunState.Waiting); await Task.Delay(250, token).ConfigureAwait(false); }
            }
        }

        public async Task<(ScriptVisionFrame, ScriptText)> WaitTextAsync(ScriptRect region, Func<string, bool> match, int seconds = 120)
        {
            var elapsed = Stopwatch.StartNew();
            var pauseAtStart = _paused;
            while (elapsed.Elapsed - (_paused - pauseAtStart) < TimeSpan.FromSeconds(seconds))
            {
                var frame = await ReadAsync(region).ConfigureAwait(false);
                var found = Unique(frame, match);
                if (found is not null) return (frame, found);
                await Task.Delay(500, token).ConfigureAwait(false);
            }
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
            token.ThrowIfCancellationRequested();
            if (Target is null || Stopwatch.GetElapsedTime(_capturedAt) > TimeSpan.FromSeconds(5))
                throw new InvalidOperationException("点击前截图已过期，暂停并保留窗口。");
            try { await vision.ClickAsync(Target, x, y, token).ConfigureAwait(false); }
            catch (ScriptTargetUnavailableException)
            {
                await ReadAsync(new(0, 0, 950, 600)).ConfigureAwait(false);
                throw new ReobserveException();
            }
        }
    }

    /// <summary>仅在原生端明确拒绝输入前使用；输入回执不确定绝不进入自动重试。</summary>
    private sealed class ReobserveException : Exception;
}
