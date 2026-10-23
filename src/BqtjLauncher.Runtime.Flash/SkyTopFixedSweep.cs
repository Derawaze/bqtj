using BqtjLauncher.Application;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>天空顶先向上再沿顶向右，另为可见剩余材料生成一次补捡路线，避免在错误的出口矩形内空扫。</summary>
internal static class SkyTopFixedSweep
{
    internal sealed record Settings(int MaxRiseMilliseconds, int MaxRightMilliseconds, int RowSpacing, int PulseMilliseconds,
        ScriptRect? MiniMap = null);
    internal sealed record Pulse(char Key, char? Second, int Milliseconds);

    /// <summary>目标材料确定拾取区域；40px内的血石帮助覆盖同一小堆，远处或单独血石不引起绕路。</summary>
    internal static ScriptRect? PickupArea(IReadOnlyList<SkyTopDropVision.Drop> visible)
    {
        var targets = visible.Where(item => item.Required).Select(item => item.Bounds).ToArray();
        if (targets.Length == 0) return null;
        var hints = visible.Where(item => item.Kind == SkyTopDropVision.DropKind.BloodStone
            && targets.Any(target => item.Bounds.X <= target.X + target.Width + 40 && item.Bounds.X + item.Bounds.Width >= target.X - 40
                && item.Bounds.Y <= target.Y + target.Height + 40 && item.Bounds.Y + item.Bounds.Height >= target.Y - 40))
            .Select(item => item.Bounds);
        var boxes = targets.Concat(hints).ToArray();
        var left = boxes.Min(box => box.X) - 20; var top = boxes.Min(box => box.Y) - 20;
        return new(left, top, boxes.Max(box => box.X + box.Width) - left + 20,
            boxes.Max(box => box.Y + box.Height) - top + 20);
    }

    /// <summary>必须靠近画面边缘，且一次已确认短按后人物不再移动；出口仍平移时不能把镜头跟随误当碰壁。</summary>
    internal static bool AtBoundary(ScriptRect? before, ScriptRect? oldExit, ScriptRect? after, ScriptRect? newExit, bool horizontal)
    {
        if (before is not { } first || after is not { } last) return false;
        if (horizontal ? last.X + last.Width < 910 : last.Y > 60) return false;
        if (Math.Abs(horizontal ? last.X - first.X : last.Y - first.Y) > 2) return false;
        if (oldExit is { } old && newExit is { } current
            && Math.Abs(horizontal ? current.X + current.Width / 2 - old.X - old.Width / 2
                : current.Y + current.Height / 2 - old.Y - old.Height / 2) > 4) return false;
        return true;
    }

    /// <summary>人物相对出口的位移消除镜头平移；两个轴分别用短按实测，不能按系统倍率直接猜速度。</summary>
    internal static double Speed(ScriptRect before, ScriptRect oldExit, ScriptRect after, ScriptRect newExit, bool horizontal, int milliseconds)
    {
        var delta = horizontal ? after.X - newExit.X - before.X + oldExit.X : after.Y - newExit.Y - before.Y + oldExit.Y;
        if (Math.Abs(delta) < 5) throw new InvalidOperationException("固定扫取未确认人物移动，请检查游戏是否暂停或按键被遮挡。");
        return Math.Clamp(Math.Abs(delta) / (double)milliseconds, .15, 2);
    }

    /// <summary>先靠近可见材料的左上侧，再只扫右、下、左一轮；右移在原两倍覆盖上增加20%，下降保持原首行间距。</summary>
    internal static IReadOnlyList<Pulse> Build(ScriptRect hero, Settings settings, double speedX, double speedY, ScriptRect area)
    {
        var x = hero.X + hero.Width / 2d; var y = hero.Y + hero.Height / 2d;
        var left = area.X; var right = area.X + area.Width * 2.4;
        var steps = new List<Pulse>();
        var rows = Math.Max(1, (int)Math.Ceiling(area.Height / (double)settings.RowSpacing));
        var lower = area.Y + area.Height / (double)rows;
        Move(left, area.Y);
        Move(right, area.Y); Move(right, lower); Move(left, lower);
        return steps;

        void Move(double targetX, double targetY)
        {
            var dx = targetX - x; var dy = targetY - y;
            var horizontal = Math.Abs(dx) / speedX; var vertical = Math.Abs(dy) / speedY;
            var diagonal = Math.Min(horizontal, vertical);
            if (diagonal < 10) diagonal = 0;
            if (diagonal >= 10) Add(dy > 0 ? 'S' : 'W', dx > 0 ? 'D' : 'A', diagonal);
            if (horizontal - diagonal >= 10) Add(dx > 0 ? 'D' : 'A', null, horizontal - diagonal);
            if (vertical - diagonal >= 10) Add(dy > 0 ? 'S' : 'W', null, vertical - diagonal);
            x = targetX; y = targetY;
        }
        void Add(char key, char? second, double duration)
        {
            var remaining = (int)Math.Round(duration);
            while (remaining > 0)
            {
                var milliseconds = Math.Min(settings.PulseMilliseconds, remaining);
                steps.Add(new(key, second, milliseconds)); remaining -= milliseconds;
            }
        }
    }

}
