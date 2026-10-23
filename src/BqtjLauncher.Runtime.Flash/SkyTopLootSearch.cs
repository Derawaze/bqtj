using System.IO;
using BqtjLauncher.Application;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>按本关候选点短步搜索，找到材料即交还拾取；路线来自资源，不套用其他地图。</summary>
internal sealed class SkyTopLootSearch(SkyTopLootSearch.Plan? plan = null)
{
    internal sealed record Leg(string Keys, int Pulses, int Milliseconds, string Label);
    internal sealed record Plan(Leg[] Legs, int TimeoutSeconds, ScriptRect? MiniMap = null);
    private readonly Plan _plan = plan ?? SkyTopDropVision.Load("天空顶").Search
        ?? throw new InvalidDataException("缺少关卡搜索参数，请更新脚本资源包。");
    private int _applied;
    private TimeSpan? _startedAt;
    internal sealed record Step(char? Key, int Milliseconds, bool Finished, string Stage, char? SecondKey = null);

    internal Step Next(TimeSpan now)
    {
        _startedAt ??= now;
        if (_applied >= _plan.Legs.Sum(leg => leg.Pulses))
            return new(null, 0, true, "本关候选刷怪点搜索已结束");
        if (now - _startedAt >= TimeSpan.FromSeconds(_plan.TimeoutSeconds))
            throw new TimeoutException("寻找掉落带超过30秒，搜索尚未完成，已暂停并保留窗口。");
        var remaining = _applied;
        var leg = _plan.Legs.First(candidate => { if (remaining < candidate.Pulses) return true; remaining -= candidate.Pulses; return false; });
        return new(leg.Keys[0], leg.Milliseconds, false, "寻找掉落区：" + leg.Label,
            leg.Keys.Length == 2 ? leg.Keys[1] : null);
    }

    /// <summary>仅在本次短按已松键并收到回执后计数；输入被明确拒绝不消耗搜索步数。</summary>
    internal void Applied() => _applied++;
}
