using BqtjLauncher.Application;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>确认观察的是首领掉落区；材料按路线逐处复查，空材料局用密集掉落本体确认区域。</summary>
internal sealed class SkyTopLootSurvey
{
    private bool _materialsObserved;

    internal bool Observe(IReadOnlyList<SkyTopDropVision.Drop> drops, ScriptRect player)
    {
        if (drops.Any(item => item.Required)) _materialsObserved = true;
        // 一旦登记材料，路线会保留并验证每个原位置；无需最后强行把整条带挤进一张截图。
        if (_materialsObserved) return true;
        var candidates = drops.Where(item => item.Kind is not (SkyTopDropVision.DropKind.AmmoBox or SkyTopDropVision.DropKind.HealthPotion)).ToArray();
        foreach (var item in candidates)
        {
            var cluster = candidates.Where(other => Math.Abs(other.Bounds.X - item.Bounds.X) <= 180 && Math.Abs(other.Bounds.Y - item.Bounds.Y) <= 80).ToArray();
            if (cluster.Length < 2) continue;
            var body = new ScriptRect(player.X - 55, player.Y - 55, player.Width + 110, player.Height + 110);
            if (cluster.All(drop => drop.Bounds.X >= 15 && drop.Bounds.X + drop.Bounds.Width <= 935
                && drop.Bounds.Y >= 73 && drop.Bounds.Y + drop.Bounds.Height <= 475
                && !SkyTopSceneVision.Intersects(drop.Bounds, SkyTopSceneVision.RewardOverlay)
                && !SkyTopSceneVision.Intersects(drop.Bounds, body))) return true;
        }
        return false;
    }
}
