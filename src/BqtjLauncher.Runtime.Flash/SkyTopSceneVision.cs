using System.Text.RegularExpressions;
using BqtjLauncher.Application;
using BqtjLauncher.Domain;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>天空顶战斗HUD及少量几何锚点；只处理950×600同帧像素，不读取桌面或猜账号身份。</summary>
internal static class SkyTopSceneVision
{
    internal sealed record Round(int Finished, int Total, int Seconds);
    internal sealed record LootGroup(ScriptRect Bounds, bool TouchesRewardOverlay, string Name = "");
    // 抽奖栏是游戏自身的固定遮挡，不是可关闭的弹窗；其下沿附近的部分图案仍可能属于被遮挡物品。
    internal static readonly ScriptRect RewardOverlay = new(260, 0, 425, 73);

    /// <summary>先确认生命HUD，再检查绿色AI字样的纵向范围；顶部角色的薄血条不能冒充AI标记。</summary>
    internal static bool? ReadBattleAi(ScriptPixels image)
    {
        var orange = 0; var green = 0; var firstGreenRow = 23; var lastGreenRow = -1;
        for (var y = 10; y < 24; y++) for (var x = 60; x < 190; x++)
        {
            var i = (y * image.Width + x) * 3;
            if (image.Bgr[i + 2] > 180 && image.Bgr[i + 1] is > 70 and < 220 && image.Bgr[i] < 80) orange++;
        }
        if (orange < 80) return null;
        for (var y = 6; y < 23; y++) for (var x = 6; x < 23; x++)
        {
            var i = (y * image.Width + x) * 3;
            if (image.Bgr[i + 1] > 150 && image.Bgr[i + 1] - image.Bgr[i + 2] > 70 && image.Bgr[i + 1] - image.Bgr[i] > 70)
            {
                green++; firstGreenRow = Math.Min(firstGreenRow, y); lastGreenRow = Math.Max(lastGreenRow, y);
            }
        }
        // 真实AI字样高约10px，现场误判的等级/血条只有3px；不靠绿色像素总量推断开关。
        return green >= 8 && lastGreenRow - firstGreenRow + 1 >= 7;
    }

    /// <summary>抽奖栏在实际通关时立即出现，比末波小字和旋转出口稳定；只接受固定栏内的完整提示。</summary>
    internal static bool HasClearBanner(IReadOnlyList<ScriptText> text)
        => text.Any(line => line.Confidence >= .8 && Intersects(line.Bounds, RewardOverlay)
            && string.Concat(line.Text.Where(character => !char.IsWhiteSpace(character))) == "通关抽奖获得");

    internal static Round? ReadRound(IReadOnlyList<ScriptText> text)
    {
        var lines = text.Where(line => line.Bounds.X >= 800 && line.Bounds.Y < 75).OrderBy(line => line.Bounds.X);
        var value = string.Join(" ", lines.Select(line => line.Text));
        // OCR有时把“7/7 05:32”连成“7/705:32”；分钟固定两位，避免把705误作总波数。
        var match = Regex.Match(value, @"(?<!\d)(\d{1,2})\s*/\s*(\d{1,2}?)\s*(\d{2})\s*[:：]([0-5]\d)(?!\d)");
        if (!match.Success) return null;
        var done = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var total = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
        return total > 0 && done <= total && seconds < 60 ? new(done, total, minutes * 60 + seconds) : null;
    }

    /// <summary>战后角色由场景内唯一的绿色横向生命条定位；HUD固定血条及掉落小色块不参与。</summary>
    internal static ScriptRect? FindPlayer(ScriptPixels image)
        => FindPlayer(image, 490);

    /// <summary>上沿移动只查最上方23像素：角色到顶时血条落在此处，下方绿色宝石不参与边界判断。</summary>
    internal static ScriptRect? FindTopEdgePlayer(ScriptPixels image)
        => FindPlayer(image, 23);

    private static ScriptRect? FindPlayer(ScriptPixels image, int scanHeight)
    {
        var bars = new List<ScriptRect>();
        for (var y = 0; y < scanHeight; y++)
        {
            var start = -1;
            for (var x = 0; x <= image.Width; x++)
            {
                var green = x < image.Width && IsHealth(image, x, y);
                if (green && start < 0) start = x;
                if (green || start < 0) continue;
                var width = x - start;
                // 生命条是薄横条；弹药箱和品质光团也会出现绿色长段，纵向厚度必须排除。
                var center = start + width / 2;
                var thick = y >= 4 && IsHealth(image, center, y - 4) || y + 4 < image.Height && IsHealth(image, center, y + 4);
                // 右上角实测可见连续段仅23px；容纳这一像素差，仍以薄横条及唯一候选排除掉落绿光。
                if (width is >= 23 and <= 85 && !thick && !bars.Any(bar => Math.Abs(bar.Y - y) <= 5 && Math.Abs(bar.X - start) <= 8))
                    bars.Add(new(start, y, width, 3));
                start = -1;
            }
        }
        if (bars.Count != 1) return null;
        var found = bars[0];
        return new(found.X + found.Width / 2 - 6, found.Y + 38, 12, 20);
    }

    /// <summary>只把关卡优先材料交给路线；宝箱和装备可顺带拾取，不阻挡完成。</summary>
    internal static IReadOnlyList<LootGroup> FindLootGroups(ScriptPixels image, ScriptRect? player, ScriptRect? exit, SkyTopDropVision.Catalog? catalog = null)
        => SkyTopDropVision.Find(image, player, exit, catalog).Where(item => item.Required).Select(item => new LootGroup(item.Bounds, Intersects(Expand(item.Bounds, 12), RewardOverlay), item.Name)).ToArray();

    internal static bool Intersects(ScriptRect a, ScriptRect b)
        => a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
    private static ScriptRect Expand(ScriptRect box, int amount) => new(box.X - amount, box.Y - amount, box.Width + amount * 2, box.Height + amount * 2);

    /// <summary>拾取期间不在大块游戏面板上发移动；固定抽奖栏仅占上沿，不属于阻挡面板。</summary>
    internal static bool HasBlockingPanel(ScriptPixels image)
    {
        var dark = 0; var total = 0;
        for (var y = 100; y < 460; y += 10) for (var x = 150; x < 800; x += 10)
        {
            var offset = (y * image.Width + x) * 3; total++;
            if (Math.Max(image.Bgr[offset], Math.Max(image.Bgr[offset + 1], image.Bgr[offset + 2])) < 45) dark++;
        }
        return dark > total * .6;
    }

    /// <summary>末波结束的金色出口箭头是大面积连通图形；不把金色卷轴或HUD文字当成完成。</summary>
    internal static ScriptRect? FindExitArrow(ScriptPixels image)
        => FindExitArrow(image, out _);

    /// <summary>诊断只返回候选形状数值，便于回放现场图片，不包含角色名或OCR正文。</summary>
    internal static ScriptRect? FindExitArrow(ScriptPixels image, out string diagnostic)
    {
        var details = new List<string>();
        var visited = new bool[image.Width * image.Height];
        var stack = new Stack<int>();
        ScriptRect? result = null;
        for (var y = 100; y < 500; y++)
            for (var x = 100; x < image.Width - 10; x++)
            {
                var index = y * image.Width + x;
                if (visited[index] || !IsGold(image, x, y)) continue;
                var left = x; var right = x; var top = y; var bottom = y; var size = 0;
                stack.Push(index); visited[index] = true;
                while (stack.TryPop(out var current))
                {
                    var px = current % image.Width; var py = current / image.Width;
                    left = Math.Min(left, px); right = Math.Max(right, px); top = Math.Min(top, py); bottom = Math.Max(bottom, py); size++;
                    Visit(px - 1, py); Visit(px + 1, py); Visit(px, py - 1); Visit(px, py + 1);
                }
                var width = right - left + 1; var height = bottom - top + 1;
                if (size < 30 || width < 20 || height < 10) continue;
                var leftEdge = 0; var rightEdge = 0; var topEdge = 0; var bottomEdge = 0;
                var verticalMiddle = 0; var horizontalMiddle = 0;
                for (var py = top; py <= bottom; py++)
                {
                    if (IsGold(image, left, py)) leftEdge++;
                    if (IsGold(image, right, py)) rightEdge++;
                    if (IsGold(image, left + width / 2, py)) verticalMiddle++;
                }
                for (var px = left; px <= right; px++)
                {
                    if (IsGold(image, px, top)) topEdge++;
                    if (IsGold(image, px, bottom)) bottomEdge++;
                    if (IsGold(image, px, top + height / 2)) horizontalMiddle++;
                }
                details.Add($"{left},{top},{width},{height}: area={size},edges={leftEdge}/{rightEdge}/{topEdge}/{bottomEdge},middle={verticalMiddle}/{horizontalMiddle}");
                // 出口会旋转，尖端可能落在四边任意一边；旋转后仍要有大连通主体，排除矩形装备色块。
                if (size < 350 || width is < 14 or > 90 || height is < 14 or > 90) continue;
                var horizontalArrow = Math.Min(leftEdge, rightEdge) <= 6 && verticalMiddle >= height / 2;
                var verticalArrow = Math.Min(topEdge, bottomEdge) <= 6 && horizontalMiddle >= width / 2;
                if (!horizontalArrow && !verticalArrow) continue;
                if (result is not null) { diagnostic = string.Join(";", details); return null; }
                result = new(left, top, width, height);
            }
        diagnostic = string.Join(";", details);
        return result;

        void Visit(int x, int y)
        {
            if (x < 100 || x >= image.Width - 10 || y < 100 || y >= 500) return;
            var index = y * image.Width + x;
            if (visited[index]) return;
            visited[index] = true;
            if (IsGold(image, x, y)) stack.Push(index);
        }
    }

    /// <summary>将同一行的材料名与独立计数框关联，再交给领域规则判断两项限额；不跨行拼数字。</summary>
    internal static DailyMaterialQuota? ReadQuota(ScriptPixels pixels, IReadOnlyList<ScriptText> text)
    {
        var lines = new List<MaterialQuotaLine>();
        foreach (var line in text)
        {
            var content = line.Text; var bounds = line.Bounds;
            var label = content.Contains("冷冻宝石", StringComparison.Ordinal) || content.Contains("守护宝石", StringComparison.Ordinal)
                || content.Contains("冥王图鉴", StringComparison.Ordinal);
            if (label && !Regex.IsMatch(content, @"\d+\s*/\s*\d+"))
            {
                // 仅找右侧80px内、行中心差不超过小框半高的数字；允许12px框重叠，不借用上下行计数。
                var counters = text.Where(other => Regex.IsMatch(other.Text, @"^[\s({（\[]*\d+\s*/\s*\d+[\s)}）\]]*$")
                    && other.Bounds.X - (bounds.X + bounds.Width) is >= -12 and <= 80
                    && Math.Abs(other.Bounds.Y * 2 + other.Bounds.Height - bounds.Y * 2 - bounds.Height)
                        <= Math.Min(bounds.Height, other.Bounds.Height)).ToArray();
                if (counters.Length == 1)
                {
                    var count = counters[0]; content += count.Text;
                    var left = Math.Min(bounds.X, count.Bounds.X); var top = Math.Min(bounds.Y, count.Bounds.Y);
                    bounds = new(left, top, Math.Max(bounds.X + bounds.Width, count.Bounds.X + count.Bounds.Width) - left,
                        Math.Max(bounds.Y + bounds.Height, count.Bounds.Y + count.Bounds.Height) - top);
                }
            }
            // 绿色证据与拼接后的名称、计数仍来自同一截图，不缓存上一轮颜色。
            lines.Add(new(content, IsQuotaGreen(pixels, bounds)));
        }
        return DailyMaterialQuota.Parse(lines);
    }

    /// <summary>只看材料计数这一行的绿色笔画；青色未满计数的蓝分量较高，不能当作绿色。</summary>
    internal static bool IsQuotaGreen(ScriptPixels pixels, ScriptRect bounds)
    {
        var green = 0; var colored = 0;
        for (var y = Math.Max(0, bounds.Y); y < Math.Min(pixels.Height, bounds.Y + bounds.Height); y++)
            for (var x = Math.Max(0, bounds.X); x < Math.Min(pixels.Width, bounds.X + bounds.Width); x++)
            {
                var offset = (y * pixels.Width + x) * 3;
                var b = pixels.Bgr[offset]; var g = pixels.Bgr[offset + 1]; var r = pixels.Bgr[offset + 2];
                if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 60 && Math.Max(g, b) > 100) colored++;
                if (g >= 100 && g - r >= 40 && g - b >= 35) green++;
            }
        return green >= 8 && green >= colored * .6;
    }

    private static bool IsHealth(ScriptPixels image, int x, int y)
    {
        var offset = (y * image.Width + x) * 3;
        return image.Bgr[offset + 1] > 160 && image.Bgr[offset + 2] < 130 && image.Bgr[offset] < 140;
    }

    private static bool IsGold(ScriptPixels image, int x, int y)
    {
        var offset = (y * image.Width + x) * 3;
        return image.Bgr[offset + 2] > 180 && image.Bgr[offset + 1] is > 90 and < 225 && image.Bgr[offset] < 75;
    }
}
