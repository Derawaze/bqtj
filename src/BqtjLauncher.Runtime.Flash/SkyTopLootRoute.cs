using BqtjLauncher.Application;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>短脉冲决策；回执表示松键，下一次决策必须使用新的同帧观察。</summary>
internal sealed record LootMove(char? Key, int Milliseconds, bool Complete, string Stage, char? SecondKey = null);

/// <summary>每局独立的材料清单、掉落带规划与经过后复查。不因移出视野或人物遮挡而注销目标。</summary>
internal sealed class SkyTopLootRoute
{
    private const double PickupRadius = 32, RevealDistance = 80;
    private readonly List<Target> _inventory = [];
    private readonly Queue<Point> _waypoints = new();
    private readonly List<Target> _active = [];
    private Point? _previousPlayer, _progressGoal;
    private LootMove? _lastMove;
    private TimeSpan _progressAt;
    private double _bestDistance = double.MaxValue, _speedX = .5, _speedY = .5;
    private int _emptyFrames;

    private readonly record struct Point(double X, double Y)
    {
        public static Point operator +(Point a, Point b) => new(a.X + b.X, a.Y + b.Y);
        public static Point operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);
        public static Point operator -(Point a) => new(-a.X, -a.Y);
        public static Point operator *(Point a, double b) => new(a.X * b, a.Y * b);
        public double Length => Math.Sqrt(X * X + Y * Y);
    }
    private sealed class Target(ScriptRect bounds, string name, bool uncertain)
    {
        public ScriptRect Bounds = bounds;
        public readonly string Name = name;
        public bool Uncertain = uncertain, Passed, Confirmed;
        public int MissingFrames, Attempts;
    }

    /// <summary>scene是累计镜头平移；清单使用场景坐标，屏幕外的旧目标仍属于本局。</summary>
    internal LootMove Observe(ScriptRect player, ScriptRect scene, IReadOnlyList<SkyTopSceneVision.LootGroup> groups, TimeSpan now,
        IReadOnlyList<ScriptRect>? blockers = null, bool sceneReliable = true, bool areaObserved = true)
    {
        var camera = new Point(scene.X, scene.Y);
        var hero = Center(player) - camera;
        if (!sceneReliable)
        {
            _previousPlayer = null; _lastMove = null;
            return new(null, 0, false, "镜头配准尚未确认，松键重新观察");
        }
        // 按图案和邻近位置更新旧记录；已确认目标重新出现时重新纳入，不能沿用旧完成状态。
        var seen = new HashSet<Target>();
        foreach (var group in groups)
        {
            var bounds = Translate(group.Bounds, -camera);
            var item = _inventory.Where(item => item.Name == group.Name && !seen.Contains(item)
                    && ((Center(item.Bounds) - Center(bounds)).Length <= 42 || SkyTopSceneVision.Intersects(item.Bounds, bounds)))
                .MinBy(item => (Center(item.Bounds) - Center(bounds)).Length);
            if (item is null)
            { item = new(bounds, group.Name, group.TouchesRewardOverlay); _inventory.Add(item); }
            else
            {
                // 堆叠晶体被收走一部分后框会缩小；仍重叠的是同一堆，丢弃旧入口并按剩余部分重规划。
                if ((Center(item.Bounds) - Center(bounds)).Length > 20 && _active.Contains(item)) _waypoints.Clear();
                item.Bounds = bounds; item.Uncertain |= group.TouchesRewardOverlay;
            }
            if (item.Confirmed) { item.Confirmed = false; item.Passed = false; }
            item.MissingFrames = 0; seen.Add(item);
        }
        Point? travelledFrom = null;
        if (_previousPlayer is { } before && _lastMove is { } movement)
        {
            // 只记录输入后实际经过的线段，发出路线、镜头自己移动都不算拾取动作证据。
            var delta = hero - before;
            if (delta.Length >= 3)
            {
                travelledFrom = before;
                foreach (var item in _inventory.Where(item => !item.Confirmed))
                    if (DistanceToSegment(Center(item.Bounds), before, hero) <= PickupRadius) item.Passed = true;
            }
            Calibrate(delta.X, 'D', 'A', ref _speedX); Calibrate(delta.Y, 'S', 'W', ref _speedY);
            void Calibrate(double actual, char positive, char negative, ref double speed)
            {
                if (Math.Abs(actual) >= 5 && (actual > 0 && (movement.Key == positive || movement.SecondKey == positive)
                    || actual < 0 && (movement.Key == negative || movement.SecondKey == negative)))
                    speed = Math.Clamp(Math.Abs(actual) / movement.Milliseconds, .15, 2);
            }
        }
        _previousPlayer = hero; _lastMove = null;
        foreach (var item in _inventory.Where(item => !item.Confirmed && !seen.Contains(item)))
        {
            var screen = Translate(item.Bounds, camera);
            var body = new ScriptRect(player.X - 55, player.Y - 55, player.Width + 110, player.Height + 110);
            var clear = screen.X >= 0 && screen.Y >= 73 && screen.X + screen.Width <= 950 && screen.Y + screen.Height <= 490
                && !SkyTopSceneVision.Intersects(screen, body) && !SkyTopSceneVision.Intersects(screen, SkyTopSceneVision.RewardOverlay)
                && !(blockers?.Any(box => SkyTopSceneVision.Intersects(screen, box)) ?? false);
            if (!item.Passed || !clear) { item.MissingFrames = 0; continue; }
            // 普通清晰帧一次即可；曾在抽奖边缘的目标补一次确认，不固定等一秒或三秒。
            if (++item.MissingFrames >= (item.Uncertain ? 2 : 1)) item.Confirmed = true;
        }
        var pending = _inventory.Where(item => !item.Confirmed).ToArray();
        if (pending.Length == 0)
        {
            _waypoints.Clear(); _active.Clear();
            if (!areaObserved) { _emptyFrames = 0; return new(null, 0, false, "等待首领掉落区进入可观察范围"); }
            var complete = _inventory.Count > 0 || ++_emptyFrames >= 2;
            return new(null, 0, complete, complete ? $"已复查{_inventory.Count}处材料，掉落带拾取完成" : "复查首领掉落区，确认本轮无优先材料");
        }
        _emptyFrames = 0;
        // 首次速度尚未校准时可能跨过入口，经过即算抵达，不为贴到旧坐标倒车。
        while (_waypoints.TryPeek(out var waypoint) && ((waypoint - hero).Length <= 15
            || travelledFrom is { } from && DistanceToSegment(waypoint, from, hero) <= 15)) _waypoints.Dequeue();
        if (_waypoints.Count == 0)
        {
            if (_active.Any(item => !item.Confirmed && item.Passed && !seen.Contains(item) && item.MissingFrames > 0))
                return new(null, 0, false, "补充一帧清晰复查");
            _active.Clear();
            var band = BuildBands(pending).MinBy(items => EntryCost(items, hero))!;
            var (start, end) = Endpoints(band);
            if ((end - hero).Length < (start - hero).Length) (start, end) = (end, start);
            var direction = end - start;
            if (direction.Length < 1) direction = start - hero;
            if (direction.Length < 1) direction = new(1, 0);
            direction *= 1 / direction.Length;
            foreach (var item in band)
            {
                if (++item.Attempts > 3) throw new InvalidOperationException("材料多次经过仍未收走，可能背包已满；已暂停并保留窗口。");
                _active.Add(item);
            }
            if ((start - hero).Length > 15) _waypoints.Enqueue(start);
            // 宽晶体堆的旧外框比单个图标大；固定80px可能仍被人物遮住，必须留出整框复查距离。
            var clearance = Math.Max(RevealDistance, band.Max(item => Math.Abs(direction.X) * (item.Bounds.Width / 2d + player.Width / 2d + 55)
                + Math.Abs(direction.Y) * (item.Bounds.Height / 2d + player.Height / 2d + 55)) + 12);
            _waypoints.Enqueue(end + direction * clearance);
            _progressGoal = null;
        }
        var goal = _waypoints.Peek();
        var distance = (goal - hero).Length;
        if (_progressGoal != goal || distance + 5 < _bestDistance)
        { _progressGoal = goal; _bestDistance = distance; _progressAt = now; }
        else if (now - _progressAt > TimeSpan.FromSeconds(8))
            throw new InvalidOperationException("掉落带走位未取得进展，已暂停并保留窗口。");
        var names = string.Join("、", _active.Select(item => item.Name).Distinct());
        var move = Direction(goal - hero, $"拾取{names}（待确认{pending.Length}处；屏幕目标{goal.X + camera.X:0},{goal.Y + camera.Y:0}）");
        _lastMove = move;
        return move;
    }

    /// <summary>原生端明确拒绝输入时丢弃动作证据，恢复后的画面不能算作执行了旧路线。</summary>
    internal void InputRejected() { _lastMove = null; _previousPlayer = null; }

    /// <summary>用主方向拟合带宽；明显上下分离才拆组，不依赖平台或地形模型。</summary>
    private static List<List<Target>> BuildBands(IReadOnlyList<Target> items)
    {
        var bands = items.OrderBy(item => Center(item.Bounds).X).Select(item => new List<Target> { item }).ToList();
        for (var i = 0; i < bands.Count; i++) for (var j = i + 1; j < bands.Count; j++)
        {
            var merged = bands[i].Concat(bands[j]).ToList();
            if (!FitsBand(merged)) continue;
            bands[i] = merged; bands.RemoveAt(j); i = -1; break;
        }
        return bands;
    }
    private static bool FitsBand(IReadOnlyList<Target> items)
    {
        var (origin, direction) = Axis(items);
        if (Math.Abs(direction.Y) > .7) return false;
        var projection = items.Select(item => Center(item.Bounds) - origin).ToArray();
        if (projection.Any(point => Math.Abs(point.X * direction.Y - point.Y * direction.X) > PickupRadius - 4)) return false;
        var along = projection.Select(point => point.X * direction.X + point.Y * direction.Y).Order().ToArray();
        return along.Zip(along.Skip(1), (a, b) => b - a).All(gap => gap <= 200);
    }
    private static (Point Origin, Point Direction) Axis(IReadOnlyList<Target> items)
    {
        var points = items.Select(item => Center(item.Bounds)).ToArray();
        var mean = new Point(points.Average(point => point.X), points.Average(point => point.Y));
        double xx = 0, xy = 0, yy = 0;
        foreach (var point in points) { var d = point - mean; xx += d.X * d.X; xy += d.X * d.Y; yy += d.Y * d.Y; }
        var angle = Math.Atan2(2 * xy, xx - yy) / 2;
        return (mean, new(Math.Cos(angle), Math.Sin(angle)));
    }
    private static (Point Start, Point End) Endpoints(IReadOnlyList<Target> items)
    {
        var (origin, direction) = Axis(items);
        var projection = items.Select(item => Center(item.Bounds) - origin).Select(point => point.X * direction.X + point.Y * direction.Y).ToArray();
        return (origin + direction * projection.Min(), origin + direction * projection.Max());
    }
    private static double EntryCost(IReadOnlyList<Target> band, Point hero)
    {
        var (start, end) = Endpoints(band);
        return Math.Min((start - hero).Length, (end - hero).Length) + (end - start).Length;
    }
    private LootMove Direction(Point delta, string stage)
    {
        if (Math.Min(Math.Abs(delta.X), Math.Abs(delta.Y)) >= 20)
            return new(delta.Y >= 0 ? 'S' : 'W', Duration(Math.Min(Math.Abs(delta.X) / _speedX, Math.Abs(delta.Y) / _speedY)), false, stage, delta.X >= 0 ? 'D' : 'A');
        var horizontal = Math.Abs(delta.X) >= Math.Abs(delta.Y);
        return new(horizontal ? delta.X >= 0 ? 'D' : 'A' : delta.Y >= 0 ? 'S' : 'W',
            Duration(horizontal ? Math.Abs(delta.X) / _speedX : Math.Abs(delta.Y) / _speedY), false, stage);
    }
    private static int Duration(double value) => Math.Clamp((int)Math.Round(value), 30, 140);
    private static Point Center(ScriptRect box) => new(box.X + box.Width / 2d, box.Y + box.Height / 2d);
    private static ScriptRect Translate(ScriptRect box, Point by) => box with { X = box.X + (int)Math.Round(by.X), Y = box.Y + (int)Math.Round(by.Y) };
    private static double DistanceToSegment(Point point, Point a, Point b)
    {
        var line = b - a;
        var length = line.X * line.X + line.Y * line.Y;
        if (length < 1) return (point - a).Length;
        var t = Math.Clamp(((point.X - a.X) * line.X + (point.Y - a.Y) * line.Y) / length, 0, 1);
        return (point - (a + line * t)).Length;
    }
}
