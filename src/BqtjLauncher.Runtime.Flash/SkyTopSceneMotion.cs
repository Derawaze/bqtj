using BqtjLauncher.Application;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>跟踪前景共同位移，包含顺带物品；不把云层视差或角色朝向当成材料世界坐标。</summary>
internal sealed class SkyTopSceneMotion
{
    private IReadOnlyList<SkyTopDropVision.Drop> _previous = [];
    private int _x, _y, _matches;
    private ScriptPixels? _previousPixels;
    private ScriptRect? _previousPlayer, _previousArrow;
    internal bool HasEstimate { get; private set; }
    internal string Registration { get; private set; } = "initial";
    private sealed record Pair(int Before, int After, int X, int Y);

    /// <summary>前景物品和出口跟踪场景；云层有视差，只用于缺参照时确认画面没有平移。</summary>
    internal ScriptRect Observe(ScriptPixels pixels, IReadOnlyList<SkyTopDropVision.Drop> drops, ScriptRect? player, ScriptRect? arrow)
    {
        var beforeX = _x; var beforeY = _y; var saved = _previous;
        var initial = _previousPixels is null;
        if (initial) { HasEstimate = true; Registration = "initial"; }
        else
        {
            Observe(drops);
            Registration = HasEstimate ? "source=items" : "unregistered";
            if (_matches < 2 && _previousArrow is { } old && arrow is { } current)
            {
                // 旋转尖端不用作参照，图形中心的少量摆动由拾取半径容差吸收。
                _x = beforeX + current.X + current.Width / 2 - old.X - old.Width / 2;
                _y = beforeY + current.Y + current.Height / 2 - old.Y - old.Height / 2;
                HasEstimate = true; Registration = "source=exit-center";
            }
            if (!HasEstimate)
            {
                var shift = Register(_previousPixels!, pixels, _previousPlayer, player, _previousArrow, arrow, out var diagnostic);
                Registration = diagnostic;
                // 实机云层会产生不同于前景的视差，非零背景平移不能直接写入材料坐标。
                if (shift is { } still && Math.Abs(still.Item1) <= 2 && Math.Abs(still.Item2) <= 2)
                { HasEstimate = true; Registration += ", source=stationary"; }
            }
        }
        if (HasEstimate)
        {
            _previousPixels = pixels; _previousPlayer = player; _previousArrow = arrow; _previous = drops;
        }
        else { _x = beforeX; _y = beforeY; _previous = saved; }
        return new(_x, _y, 1, 1);
    }

    /// <summary>稀疏纹理块匹配平移，排除固定HUD、人物及旋转箭头；没有足够纹理时明确交给其他参照。</summary>
    private static (int, int)? Register(ScriptPixels before, ScriptPixels after, ScriptRect? oldPlayer, ScriptRect? player,
        ScriptRect? oldArrow, ScriptRect? arrow, out string diagnostic)
    {
        var oldGray = Gray(before); var newGray = Gray(after);
        const int width = 237, height = 150;
        var samples = new List<(int X, int Y, int Value, int Texture)>();
        for (var y = 25; y < 118; y += 4) for (var x = 8; x < 230; x += 4)
        {
            if (Masked(x * 4, y * 4, oldPlayer, oldArrow)) continue;
            var at = y * width + x;
            var texture = Math.Abs(oldGray[at - 1] - oldGray[at + 1]) + Math.Abs(oldGray[at - width] - oldGray[at + width]);
            if (texture >= 20) samples.Add((x, y, oldGray[at], texture));
        }
        var selected = samples.OrderByDescending(item => item.Texture).Take(192).ToArray();
        diagnostic = $"texture={selected.Length}";
        if (selected.Length < 48) return null;
        var best = double.MaxValue; var bestX = 0; var bestY = 0;
        var scores = new List<(int X, int Y, double Score)>();
        for (var dy = -55; dy <= 55; dy++) for (var dx = -55; dx <= 55; dx++)
        {
            var error = 0; var count = 0;
            foreach (var sample in selected)
            {
                var x = sample.X + dx; var y = sample.Y + dy;
                if (x < 5 || x >= width - 5 || y < 24 || y >= height - 28 || Masked(x * 4, y * 4, player, arrow)) continue;
                error += Math.Min(60, Math.Abs(sample.Value - newGray[y * width + x])); count++;
            }
            if (count < Math.Max(48, selected.Length / 2)) continue;
            var score = error / (double)count;
            scores.Add((dx * 4, dy * 4, score));
            if (score < best) { best = score; bestX = dx * 4; bestY = dy * 4; }
        }
        diagnostic += $", coarse={best:F1},shift={bestX}/{bestY}";
        // 高纹理边缘遇到非四像素平移时均值误差会放大；最终是否可信由原像素复核决定。
        if (best > 24) return null;
        // 重复云纹/平台可能在不同位置同样匹配，存在竞争位置时不签发可信平移。
        if (scores.Any(item => Math.Abs(item.X - bestX) + Math.Abs(item.Y - bestY) >= 20 && item.Score < best + 1.5)) return null;
        // 灰度块先找四像素级平移，再用原始像素细化，避免小步走位累计四舍五入误差。
        var refined = double.MaxValue; var result = (bestX, bestY); var agreement = 0;
        for (var dy = bestY - 3; dy <= bestY + 3; dy++) for (var dx = bestX - 3; dx <= bestX + 3; dx++)
        {
            var errors = new List<int>(); var count = 0; var matches = 0;
            foreach (var sample in selected)
            {
                var x = sample.X * 4 + dx; var y = sample.Y * 4 + dy;
                if (x < 0 || x >= after.Width || y < 95 || y >= 490 || Masked(x, y, player, arrow)) continue;
                var oldAt = (sample.Y * 4 * before.Width + sample.X * 4) * 3; var at = (y * after.Width + x) * 3;
                var difference = 0;
                for (var channel = 0; channel < 3; channel++) difference += Math.Abs(before.Bgr[oldAt + channel] - after.Bgr[at + channel]);
                errors.Add(difference); count++; if (difference < 45) matches++;
            }
            if (count < 48) continue;
            // 人物喷焰、光效和消失物品是局部变化，用多数静态纹理复核，不让少数动画抬高整帧误差。
            var score = errors.Order().Take(count * 3 / 5).Average();
            var percent = matches * 100 / count;
            var better = score < refined - .01 || Math.Abs(score - refined) <= .01
                && (percent > agreement || percent == agreement && Math.Abs(dx - bestX) + Math.Abs(dy - bestY)
                    < Math.Abs(result.Item1 - bestX) + Math.Abs(result.Item2 - bestY));
            if (!better) continue;
            refined = score; result = (dx, dy); agreement = percent;
        }
        diagnostic += $", refined={refined:F1},agreement={agreement}";
        return refined < 42 && agreement >= 55 ? result : null;

        static bool Masked(int x, int y, ScriptRect? body, ScriptRect? exit)
            => Inside(body, 75, x, y) || Inside(exit, 35, x, y);
    }

    private static bool Inside(ScriptRect? box, int amount, int x, int y)
        => box is { } b && x >= b.X - amount && x < b.X + b.Width + amount && y >= b.Y - amount && y < b.Y + b.Height + amount;

    private static byte[] Gray(ScriptPixels image)
    {
        var result = new byte[237 * 150];
        for (var y = 0; y < 150; y++) for (var x = 0; x < 237; x++)
        {
            var sum = 0;
            for (var dy = 0; dy < 4; dy++) for (var dx = 0; dx < 4; dx++)
            { var at = ((y * 4 + dy) * image.Width + x * 4 + dx) * 3; sum += image.Bgr[at] + image.Bgr[at + 1] + image.Bgr[at + 2]; }
            result[y * 237 + x] = (byte)(sum / 48);
        }
        return result;
    }

    internal ScriptRect Observe(IReadOnlyList<SkyTopDropVision.Drop> current)
    {
        var candidates = new List<Pair>();
        for (var before = 0; before < _previous.Count; before++) for (var after = 0; after < current.Count; after++)
        {
            if (_previous[before].Name != current[after].Name) continue;
            var a = _previous[before].Bounds; var b = current[after].Bounds;
            candidates.Add(new(before, after, b.X + b.Width / 2 - a.X - a.Width / 2, b.Y + b.Height / 2 - a.Y - a.Height / 2));
        }
        var best = new List<Pair>();
        foreach (var candidate in candidates)
        {
            var matched = new List<Pair>();
            foreach (var pair in candidates.Where(pair => Math.Abs(pair.X - candidate.X) <= 8 && Math.Abs(pair.Y - candidate.Y) <= 8)
                         .OrderBy(pair => Math.Abs(pair.X - candidate.X) + Math.Abs(pair.Y - candidate.Y)))
            {
                // 同名多件也只能一对一匹配，不能用一个图标制造多票相机位移。
                if (matched.Any(item => item.Before == pair.Before || item.After == pair.After)) continue;
                matched.Add(pair);
            }
            if (matched.Count > best.Count || matched.Count == best.Count && matched.Sum(pair => Math.Abs(pair.X) + Math.Abs(pair.Y)) < best.Sum(pair => Math.Abs(pair.X) + Math.Abs(pair.Y)))
                best = matched;
        }
        _matches = best.Count;
        HasEstimate = best.Count > 0;
        if (HasEstimate)
        {
            // 多个物品共同位移的中位数能压掉图标轻微悬浮，不追随某个动画图案。
            _x += best.Select(pair => pair.X).Order().ElementAt(best.Count / 2);
            _y += best.Select(pair => pair.Y).Order().ElementAt(best.Count / 2);
        }
        _previous = current;
        return new(_x, _y, 1, 1);
    }
}
