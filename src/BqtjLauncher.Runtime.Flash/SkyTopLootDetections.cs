using BqtjLauncher.Application;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>材料入清单前做相邻两帧确认；用场景位置关联，不把一次动画闪光变成永久走位目标。</summary>
internal sealed class SkyTopLootDetections
{
    private List<(SkyTopDropVision.Drop Drop, ScriptRect World, int Frames)> _previous = [];
    internal bool HasCandidates { get; private set; }
    internal bool NeedsConfirmation { get; private set; }

    internal IReadOnlyList<SkyTopDropVision.Drop> Observe(IReadOnlyList<SkyTopDropVision.Drop> drops, ScriptRect camera)
    {
        var next = new List<(SkyTopDropVision.Drop Drop, ScriptRect World, int Frames)>();
        var matched = new HashSet<int>();
        foreach (var drop in drops.Where(drop => drop.Required))
        {
            var world = drop.Bounds with { X = drop.Bounds.X - camera.X, Y = drop.Bounds.Y - camera.Y };
            var index = Enumerable.Range(0, _previous.Count).Where(i => !matched.Contains(i) && _previous[i].Drop.Name == drop.Name
                    && (SkyTopSceneVision.Intersects(_previous[i].World, world) || Distance(_previous[i].World, world) <= 32))
                .OrderBy(i => Distance(_previous[i].World, world)).DefaultIfEmpty(-1).First();
            if (index >= 0) matched.Add(index);
            next.Add((drop, world, index < 0 ? 1 : _previous[index].Frames + 1));
        }
        _previous = next;
        HasCandidates = next.Count > 0;
        NeedsConfirmation = next.Any(item => item.Frames == 1);
        // 忽略项仍供镜头和区域观察使用；三种宝箱只顺带，不阻塞材料完成。
        return drops.Where(drop => !drop.Required).Concat(next.Where(item => item.Frames >= 2).Select(item => item.Drop)).ToArray();
    }

    private static double Distance(ScriptRect a, ScriptRect b)
        => Math.Abs(a.X + a.Width / 2d - b.X - b.Width / 2d) + Math.Abs(a.Y + a.Height / 2d - b.Y - b.Height / 2d);
}
