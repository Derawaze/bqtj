using System.Diagnostics;
using BqtjLauncher.Application;

namespace BqtjLauncher.Runtime.Flash;

public sealed partial class OfficialScriptExecutor
{
    private static readonly ScriptRect BagFullRegion = new(50, 515, 60, 50);
    private static readonly ScriptRect BagRegion = new(70, 15, 835, 505);
    private static readonly ScriptRect BagTabs = new(540, 50, 350, 40);
    private static readonly ScriptRect BagAction = new(710, 460, 135, 45);
    private static readonly ScriptRect BagTitle = new(680, 15, 100, 35);

    /// <summary>只在战后背包按钮明确出现“满”时整理；沿用游戏自己的品质/保留条件，不修改复选框。</summary>
    private static async Task<bool> TidyInventoryIfFullAsync(Context context)
    {
        var badge = await context.ReadAsync(BagFullRegion).ConfigureAwait(false);
        if (!HasFullMarker(badge)) return false;
        context.Stage("背包已满，整理武器和装备");
        context.Trace("inventory-full");
        while (true)
        {
            try { await context.ClickAsync(80, 575).ConfigureAwait(false); break; }
            catch (ReobserveException) { await context.ReadAsync(BagFullRegion).ConfigureAwait(false); }
        }
        await WaitInventoryPageAsync(context).ConfigureAwait(false);
        await DismantleAsync("武器").ConfigureAwait(false);
        await DismantleAsync("装备").ConfigureAwait(false);

        // “满”必须在基因体标签上方，其他分类的满包标记不能触发基因体分解。
        var (tabs, genes) = await WaitInventoryTextAsync(context, BagTabs, "基因体").ConfigureAwait(false);
        var aboveGenes = new ScriptRect(genes.Bounds.X - 5, Math.Max(0, genes.Bounds.Y - 35), genes.Bounds.Width + 10, 35);
        if (HasFullMarker(await context.ReadAsync(aboveGenes, tabs.SnapshotId).ConfigureAwait(false)))
        {
            context.Stage("基因体已满，一键分解");
            await ClickInventoryTextAsync(context, BagTabs, "基因体").ConfigureAwait(false);
            await ClickInventoryTextAsync(context, BagAction, "一键分解").ConfigureAwait(false);
            await ClickInventoryDialogAsync(context, InventoryDialogVision.Kind.Confirmation).ConfigureAwait(false);
            // 基因体只有一次确认，观察回到背包后继续，不补点武器/装备的第二次确定。
            await WaitInventoryPageAsync(context).ConfigureAwait(false);
            context.Trace("inventory-genes-complete");
        }
        context.Stage("背包整理完成，关闭背包继续拾取");
        while (true)
        {
            await WaitInventoryPageAsync(context).ConfigureAwait(false);
            try { await context.ClickAsync(880, 32).ConfigureAwait(false); break; }
            catch (ReobserveException) { }
        }
        var elapsed = Stopwatch.StartNew(); var paused = context.PausedDuration;
        while (elapsed.Elapsed - (context.PausedDuration - paused) < TimeSpan.FromSeconds(10))
        {
            var frame = await context.ReadAsync(BagRegion).ConfigureAwait(false);
            if (Unique(frame, text => text == "背包") is null) { context.Trace("inventory-complete"); return true; }
            await Task.Delay(150, context.Token).ConfigureAwait(false);
        }
        throw new InvalidOperationException("背包未关闭，已保留窗口，不继续移动或重玩。");

        async Task DismantleAsync(string category)
        {
            context.Stage("整理" + category + "：一键拆解并确认结果", "整理背包：" + category);
            await ClickInventoryTextAsync(context, BagTabs, category).ConfigureAwait(false);
            await ClickInventoryTextAsync(context, BagAction, "一键拆解").ConfigureAwait(false);
            await ClickInventoryDialogAsync(context, InventoryDialogVision.Kind.Confirmation).ConfigureAwait(false);
            // 第二个确定只属于结果框；原确认框、未知提示或动画未结束时均不能连点。
            await ClickInventoryDialogAsync(context, InventoryDialogVision.Kind.Result).ConfigureAwait(false);
            await WaitInventoryPageAsync(context).ConfigureAwait(false);
            context.Trace("inventory-category-complete", result: category);
        }
    }

    /// <summary>仅接受独立“满”标记，满级、数量等普通背包文字不触发整理。</summary>
    private static bool HasFullMarker(ScriptVisionFrame frame)
        => frame.Text.Any(line => line.Confidence >= .75 && Compact(line.Text) == "满");

    /// <summary>装备详情会覆盖分类、拆解和弹窗；每次观察前将本会话悬停移到标题，不移动系统鼠标。</summary>
    private static async Task<ScriptSceneFrame> CaptureInventoryAsync(Context context)
    {
        while (true)
        {
            // 先取得新观察以绑定目标并处理暂停；移开悬停后重新截图，不能复用仍含详情框的旧快照。
            await context.CaptureAsync().ConfigureAwait(false);
            try { await context.HoverAsync(720, 32).ConfigureAwait(false); }
            catch (ReobserveException) { continue; }
            return await context.CaptureAsync().ConfigureAwait(false);
        }
    }

    /// <summary>分类切换、确认结果后都可能再次触发物品悬停，等待按钮时每帧重新清除。</summary>
    private static async Task<(ScriptVisionFrame, ScriptText)> WaitInventoryTextAsync(Context context, ScriptRect region, string text)
    {
        var elapsed = Stopwatch.StartNew(); var paused = context.PausedDuration;
        while (elapsed.Elapsed - (context.PausedDuration - paused) < TimeSpan.FromSeconds(20))
        {
            var scene = await CaptureInventoryAsync(context).ConfigureAwait(false);
            var frame = await context.ReadAsync(region, scene.Frame.SnapshotId).ConfigureAwait(false);
            if (Unique(frame, value => value == text) is { } found) return (frame, found);
            await Task.Delay(150, context.Token).ConfigureAwait(false);
        }
        throw new TimeoutException("背包操作未出现：" + text + "，已保留窗口。");
    }

    /// <summary>只点击清除遮挡后识别到的按钮；暂停恢复后重新观察，不沿用旧坐标。</summary>
    private static async Task ClickInventoryTextAsync(Context context, ScriptRect region, string text)
    {
        while (true)
        {
            var (_, found) = await WaitInventoryTextAsync(context, region, text).ConfigureAwait(false);
            try { await context.ClickAsync(found.Bounds.X + found.Bounds.Width / 2, found.Bounds.Y + found.Bounds.Height / 2).ConfigureAwait(false); return; }
            catch (ReobserveException) { }
        }
    }

    /// <summary>确认框点左侧(420,315)，结果框点中央(475,315)；纯截图轮询，不等OCR或固定秒数。</summary>
    private static async Task ClickInventoryDialogAsync(Context context, InventoryDialogVision.Kind expected)
    {
        var elapsed = Stopwatch.StartNew(); var paused = context.PausedDuration;
        while (elapsed.Elapsed - (context.PausedDuration - paused) < TimeSpan.FromSeconds(10))
        {
            var scene = await CaptureInventoryAsync(context).ConfigureAwait(false);
            if (InventoryDialogVision.Read(scene.Pixels) == expected)
            {
                var x = expected == InventoryDialogVision.Kind.Confirmation ? 420 : 475;
                context.Trace("inventory-dialog-ready", x: x, y: 315, duration: elapsed.ElapsedMilliseconds, result: expected.ToString());
                try { await context.ClickAsync(x, 315).ConfigureAwait(false); return; }
                catch (ReobserveException) { continue; }
            }
            await Task.Delay(50, context.Token).ConfigureAwait(false);
        }
        throw new TimeoutException(expected == InventoryDialogVision.Kind.Result ? "拆解结果框未出现，已保留窗口。" : "背包处理确认框未出现，已保留窗口。");
    }

    /// <summary>标题可能透过半透明弹窗被识别；同时确认弹窗按钮消失才交给下一步。</summary>
    private static async Task WaitInventoryPageAsync(Context context)
    {
        var elapsed = Stopwatch.StartNew(); var paused = context.PausedDuration;
        while (elapsed.Elapsed - (context.PausedDuration - paused) < TimeSpan.FromSeconds(20))
        {
            var scene = await CaptureInventoryAsync(context).ConfigureAwait(false);
            if (InventoryDialogVision.Read(scene.Pixels) == InventoryDialogVision.Kind.None)
            {
                var title = await context.ReadAsync(BagTitle, scene.Frame.SnapshotId).ConfigureAwait(false);
                if (Unique(title, text => text == "背包") is not null) return;
            }
            await Task.Delay(50, context.Token).ConfigureAwait(false);
        }
        throw new TimeoutException("背包处理后未回到分类页面，已保留窗口。");
    }
}
