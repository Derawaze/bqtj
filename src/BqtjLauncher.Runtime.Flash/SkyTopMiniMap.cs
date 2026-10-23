using BqtjLauncher.Application;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>小地图黄点是操控角色，绿点为队友；保留旧搜索原型，当前边界直接读取唯一黄点。</summary>
internal sealed class SkyTopMiniMap(ScriptRect region)
{
    internal readonly record struct Dot(int X, int Y);

    /// <summary>波次文字下方的小黄点不受抽奖遮挡；只接受一个紧凑标记，不能任选绿点或文字碎片。</summary>
    internal static Dot? FindPlayer(ScriptPixels pixels, ScriptRect region)
    {
        var dots = Find(pixels, region, red: false, yellow: true);
        return dots.Length == 1 ? dots[0] : null;
    }
    private Dot[] _enemies = [];

    internal void ObserveEnemies(ScriptPixels pixels, bool finalWave)
    {
        if (!finalWave) return;
        var dots = Find(pixels, region, red: true);
        // 通关红点消失后保留最后一次位置，绝不把出口箭头或绿点当成首领位置。
        if (dots.Length > 0) _enemies = dots;
    }

    internal (char Key, char? Second)? Heading(ScriptPixels pixels)
    {
        if (FindPlayer(pixels, region) is not { } player || _enemies.Length == 0) return null;
        var target = _enemies.MinBy(dot => Math.Abs(dot.X - player.X) + Math.Abs(dot.Y - player.Y));
        var horizontal = target.X - player.X; var vertical = target.Y - player.Y;
        char? x = Math.Abs(horizontal) > 2 ? horizontal > 0 ? 'D' : 'A' : null;
        char? y = Math.Abs(vertical) > 2 ? vertical > 0 ? 'S' : 'W' : null;
        return y.HasValue ? (y.Value, x) : x.HasValue ? (x.Value, null) : null;
    }

    /// <summary>只统计小地图内的1～5像素标记，排除边框、波数文字和大块背景颜色。</summary>
    internal static Dot[] Find(ScriptPixels pixels, ScriptRect region, bool red, bool yellow = false)
    {
        var seen = new HashSet<int>(); var result = new List<Dot>();
        bool Match(int x, int y)
        {
            var i = (y * pixels.Width + x) * 3;
            return yellow ? pixels.Bgr[i + 2] > 190 && pixels.Bgr[i + 1] > 160 && pixels.Bgr[i] < 110
                : red ? pixels.Bgr[i + 2] > 190 && pixels.Bgr[i + 1] < 110 && pixels.Bgr[i] < 110
                : pixels.Bgr[i + 1] > 160 && pixels.Bgr[i + 2] < 110 && pixels.Bgr[i] < 110;
        }
        for (var y = region.Y; y < region.Y + region.Height; y++) for (var x = region.X; x < region.X + region.Width; x++)
        {
            var index = y * pixels.Width + x;
            if (seen.Contains(index) || !Match(x, y)) continue;
            var pending = new Queue<Dot>(); var points = new List<Dot>(); pending.Enqueue(new(x, y)); seen.Add(index);
            while (pending.TryDequeue(out var dot))
            {
                points.Add(dot);
                for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
                {
                    var next = new Dot(dot.X + dx, dot.Y + dy); var nextIndex = next.Y * pixels.Width + next.X;
                    if (next.X < region.X || next.Y < region.Y || next.X >= region.X + region.Width || next.Y >= region.Y + region.Height
                        || seen.Contains(nextIndex) || !Match(next.X, next.Y)) continue;
                    seen.Add(nextIndex); pending.Enqueue(next);
                }
            }
            if (points.Count >= (yellow ? 3 : 1) && points.Count <= 16
                && points.Max(p => p.X) - points.Min(p => p.X) < 5 && points.Max(p => p.Y) - points.Min(p => p.Y) < 5)
                result.Add(new((int)Math.Round(points.Average(p => p.X)), (int)Math.Round(points.Average(p => p.Y))));
        }
        return result.ToArray();
    }
}
