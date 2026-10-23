using System.Text.RegularExpressions;
using BqtjLauncher.Application;
using BqtjLauncher.Domain;

namespace BqtjLauncher.Runtime.Flash;

public sealed partial class OfficialScriptExecutor
{
    /// <summary>刷关共用的同帧限额读取；白色名称和蓝/绿色小数字分开识字，颜色判断仍取原图。</summary>
    private static async Task<DailyMaterialQuota?> ReadBattleQuotaAsync(Context context, IScriptVision vision, ScriptVisionFrame frame)
    {
        var pixels = await vision.PixelsAsync(context.Target!, new(0, 0, 950, 600), frame.SnapshotId, context.Token).ConfigureAwait(false);
        var text = frame.Text.ToList();
        var quota = SkyTopSceneVision.ReadQuota(pixels, text);
        if (quota is not null) return quota;
        var labels = frame.Text.Where(line => line.Text.Contains("冷冻宝石", StringComparison.Ordinal)
            || line.Text.Contains("冥王图鉴", StringComparison.Ordinal)).ToArray();
        context.Trace("quota-tooltip", result: labels.Length == 0 ? "labels-missing" : "labels-present", textCount: labels.Length);
        foreach (var label in labels.Where(line => !Regex.IsMatch(line.Text, @"\d+\s*/\s*\d+")))
        {
            var box = label.Bounds;
            var region = new ScriptRect(Math.Max(0, box.X + box.Width - 8), Math.Max(0, box.Y - 3), 110,
                Math.Min(box.Height + 6, 600 - Math.Max(0, box.Y - 3)));
            var numbers = await context.ReadAsync(region, frame.SnapshotId, ScriptTextStyle.QuotaCounter).ConfigureAwait(false);
            var counters = numbers.Text.Where(line => Regex.IsMatch(line.Text, @"^[\s({（\[]*\d+\s*/\s*\d+[\s)}）\]]*$")).ToArray();
            text.AddRange(counters);
            context.Trace("quota-counter-read", region, result: counters.Length == 0 ? "unavailable" : "read", textCount: counters.Length);
        }
        return SkyTopSceneVision.ReadQuota(pixels, text);
    }
}
