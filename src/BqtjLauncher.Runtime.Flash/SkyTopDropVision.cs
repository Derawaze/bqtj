using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BqtjLauncher.Application;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>战后物品分类与路线分开；只为本关材料绕路，装备与宝箱顺带拾取，不用品质光晕判断种类。</summary>
internal static class SkyTopDropVision
{
    internal enum DropKind
    {
        ColdGem, GuardGem, HadesCodex, Equipment, Weapon, Voucher, Offhand, Device,
        SuperStone, DivineStone, ConversionStone, BloodStone, LifeCatalyst,
        AncientChest, LegendaryChest, RareChest, AmmoBox, HealthPotion, Material
    }
    internal sealed record Drop(DropKind Kind, string Name, ScriptRect Bounds, double Confidence, bool Overlapping, bool ShouldPickup)
    {
        // 宝箱顺带拾取；血石只辅助定位附近掉落区，均不能阻塞日常材料的完成。
        public bool Required => ShouldPickup && Kind is not (DropKind.BloodStone or DropKind.AncientChest or DropKind.LegendaryChest or DropKind.RareChest);
    }
    internal sealed record Pixel(int X, int Y, byte B, byte G, byte R);
    internal sealed record Pattern(DropKind Kind, string Name, bool Pickup, double Threshold, int Width, int Height, Pixel[] Pixels, Pixel Anchor, double Mean, double Energy);

    /// <summary>一次任务的图案与拾取规则快照；外部资源更新从下一次任务生效。</summary>
    internal sealed record Catalog(Pattern[] Patterns, bool DetectCold, bool PickupCold, SkyTopLootSearch.Plan? Search, SkyTopFixedSweep.Settings? Sweep = null);
    private static readonly Lazy<Catalog> Default = new(() => Load("天空顶"));
    internal static string ResourceDirectory => Path.Combine(AppContext.BaseDirectory, "scripts", "resources", "loot");

    /// <summary>按关卡选择候选图案；全局宝箱与该关卡目标合并，不带入其他关卡的目标。</summary>
    internal static Catalog Load(string map, string purpose = "daily-materials", string? directory = null)
    {
        directory ??= ResourceDirectory;
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(directory, "catalog.json")), JsonOptions)
            ?? throw new InvalidDataException("掉落图标目录为空。");
        if (manifest.Version != 3) throw new InvalidDataException("掉落图标目录版本不兼容，需要version=3。");
        var profile = manifest.Profiles.SingleOrDefault(item => item.Map == map && item.Purpose == purpose)
            ?? throw new InvalidDataException($"没有{map}的{purpose}拾取规则。");
        var items = manifest.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var pickup = manifest.GlobalPickup.Concat(profile.Pickup).ToHashSet(StringComparer.Ordinal);
        var recognize = profile.Recognize.Concat(pickup).ToHashSet(StringComparer.Ordinal);
        if (recognize.Any(id => !items.ContainsKey(id))) throw new InvalidDataException("拾取规则引用了不存在的物品ID。");
        var patterns = new List<Pattern>();
        foreach (var id in recognize)
        {
            var item = items[id];
            foreach (var template in item.Patterns)
            {
                if (Path.GetFileName(template.Image) != template.Image || template.Scales.Length == 0
                    || template.Scales.Any(scale => scale is < .25 or > 2) || template.Threshold is < .5 or > 1)
                    throw new InvalidDataException($"{item.Name}的图标路径、尺寸或阈值无效。");
                using var stream = File.OpenRead(Path.Combine(directory, template.Image));
                using var original = new Bitmap(stream);
                foreach (var scale in template.Scales)
                    patterns.Add(BuildPattern(original, scale, item, template, pickup.Contains(id)));
            }
        }
        var cold = manifest.Items.Single(item => item.Detector == "cold-crystal");
        if (recognize.Contains(cold.Id) && !patterns.Any(pattern => pattern.Kind == DropKind.ColdGem))
            throw new InvalidDataException("冷冻宝石缺少本体图案，请合并覆盖最新scripts资源。");
        if (profile.Search is { } search && (search.TimeoutSeconds <= 0 || search.Legs.Length == 0
            || search.Legs.Any(leg => leg.Keys is not ("W" or "A" or "S" or "D" or "WD" or "WA" or "SD" or "SA")
                || leg.Pulses <= 0 || leg.Milliseconds is < 1 or > 500)
            || search.MiniMap is { } region && (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0
                || region.X + region.Width > 950 || region.Y + region.Height > 600)))
            throw new InvalidDataException("关卡搜索方向、步数或短按时长无效。");
        if (profile.Sweep is { } sweep && (sweep.MaxRiseMilliseconds is < 50 or > 1500 || sweep.MaxRightMilliseconds is < 1 or > 5000
            || sweep.RowSpacing is < 10 or > 60 || sweep.PulseMilliseconds is < 1 or > 500
            || sweep.MiniMap is { } mapRegion && (mapRegion.X < 0 || mapRegion.Y < 0 || mapRegion.Width < 3 || mapRegion.Height < 3
                || mapRegion.X + mapRegion.Width > 950 || mapRegion.Y + mapRegion.Height > 600)))
            throw new InvalidDataException("上移/右移输入上限、补捡行距或短按时长无效，请更新脚本资源。");
        return new(patterns.ToArray(), recognize.Contains(cold.Id), pickup.Contains(cold.Id), profile.Search, profile.Sweep);
    }

    private sealed record Manifest(int Version, string[] GlobalPickup, Profile[] Profiles, Item[] Items);
    private sealed record Profile(string Map, string Purpose, string[] Pickup, string[] Recognize, SkyTopLootSearch.Plan? Search, SkyTopFixedSweep.Settings? Sweep);
    private sealed record Item(string Id, string Name, DropKind Kind, Template[] Patterns, string? Detector);
    private sealed record Template(string Image, double[] Scales, double Threshold, bool Masked);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>冷冻宝石定位堆叠晶面，其余种类匹配自身图案；未知物品不擅自标成材料。</summary>
    internal static IReadOnlyList<Drop> Find(ScriptPixels image, ScriptRect? player, ScriptRect? exit, Catalog? catalog = null)
    {
        catalog ??= Default.Value;
        var result = new List<Drop>();
        var candidates = new List<Drop>();
        // 同一帧只分桶一次。每张图案从颜色可能匹配的位置开始，避免逐图案扫描几十次全画面。
        var buckets = new List<int>?[512];
        for (var y = 0; y < 490; y++) for (var x = 0; x < image.Width; x++)
        {
            var index = y * image.Width + x; var at = index * 3;
            var bucket = (image.Bgr[at] >> 5) | ((image.Bgr[at + 1] >> 5) << 3) | ((image.Bgr[at + 2] >> 5) << 6);
            (buckets[bucket] ??= []).Add(index);
        }
        foreach (var pattern in catalog.Patterns)
        {
            var anchor = pattern.Anchor;
            for (var bucket = 0; bucket < buckets.Length; bucket++)
            {
                if (buckets[bucket] is not { } positions || MinimumError(anchor.B, bucket & 7)
                    + MinimumError(anchor.G, (bucket >> 3) & 7) + MinimumError(anchor.R, bucket >> 6) > 110) continue;
                foreach (var position in positions)
                {
                    var x = position % image.Width - anchor.X; var y = position / image.Width - anchor.Y;
                    if (x < 0 || y < 0 || x >= image.Width - pattern.Width || y >= 490 - pattern.Height) continue;
                    // 抽奖奖品可能碰到栏下沿，任何相交图案都不能作为地面材料种子；被挡物品靠固定扫取覆盖。
                    if (y < 73 && (x < 260 || x + pattern.Width > 800)) continue;
                    var reward = SkyTopSceneVision.RewardOverlay;
                    if (SkyTopSceneVision.Intersects(new(x, y, pattern.Width, pattern.Height), reward)) continue;
                    var at = position * 3;
                    var error = Math.Abs(image.Bgr[at] - anchor.B) + Math.Abs(image.Bgr[at + 1] - anchor.G) + Math.Abs(image.Bgr[at + 2] - anchor.R);
                    // 宝石/图鉴有本体纹理验证，人物外12px即可；旧55px会隐藏尚未进入拾取距离的材料。
                    // 纯颜色晶面检测仍保留较宽的喷焰排除区，避免把角色光效当作冷冻宝石。
                    var playerMargin = pattern.Kind is DropKind.ColdGem or DropKind.GuardGem or DropKind.HadesCodex ? 12 : 55;
                    if (error > 110 || Excluded(x + pattern.Width / 2, y + pattern.Height / 2, player, exit, playerMargin)) continue;
                    var score = Score(image, x, y, pattern);
                    if (score < pattern.Threshold) continue;
                    candidates.Add(new(pattern.Kind, pattern.Name, new(x, y, pattern.Width, pattern.Height), Math.Min(1, score), false, pattern.Pickup));
                }
            }
        }
        // 所有尺寸统一按相关度去重，不能让先尝试的小尺寸覆盖更可靠的本体匹配。
        foreach (var candidate in candidates.OrderByDescending(item => item.Confidence))
            if (!result.Any(item => item.Name == candidate.Name && SameItem(item.Bounds, candidate.Bounds))) result.Add(candidate);
        // 先识别具体物品，再在其余可见区域检查晶面，避免蓝色装置被误认成冷冻宝石。
        var coldSeeds = result.Where(item => item.Kind == DropKind.ColdGem).ToArray();
        result.RemoveAll(item => item.Kind == DropKind.ColdGem);
        var cold = coldSeeds.ToList();
        if (catalog.DetectCold) FindColdGroups(image, player, exit, cold, result, coldSeeds, catalog.PickupCold);
        // 同一堆晶体被枪械/图鉴切断时仍算一个观察区域；这里不推算宝石件数。
        for (var i = 0; i < cold.Count; i++) for (var j = i + 1; j < cold.Count; j++)
        {
            if (!SkyTopSceneVision.Intersects(Expand(cold[i].Bounds, 6), cold[j].Bounds)) continue;
            var a = cold[i].Bounds; var b = cold[j].Bounds;
            var union = new ScriptRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                Math.Max(a.X + a.Width, b.X + b.Width) - Math.Min(a.X, b.X), Math.Max(a.Y + a.Height, b.Y + b.Height) - Math.Min(a.Y, b.Y));
            cold[i] = cold[i] with { Bounds = union, Overlapping = true }; cold.RemoveAt(j); i = -1; break;
        }
        result.AddRange(cold);
        return result;
    }

    // 颜色桶只是加速索引；用桶内最小距离筛选，原有110阈值内的像素一个也不漏。
    private static int MinimumError(byte value, int bucket)
        => Math.Max(0, Math.Max(bucket * 32 - value, value - (bucket * 32 + 31)));

    /// <summary>完整本体图案确认身份，晶面群只扩展同一堆的范围；青色装置、云层和出口光效不能单独产生材料。</summary>
    private static void FindColdGroups(ScriptPixels image, ScriptRect? player, ScriptRect? exit, List<Drop> found,
        IReadOnlyList<Drop> known, Drop[] seeds, bool pickup)
    {
        if (seeds.Length == 0) return;
        var visited = new bool[image.Width * image.Height]; var pending = new Stack<int>();
        for (var y = 73; y < 490; y++) for (var x = 0; x < image.Width; x++)
        {
            var index = y * image.Width + x;
            if (visited[index] || !Cyan(x, y)) continue;
            var left = x; var right = x; var top = y; var bottom = y; var size = 0; var facets = 0;
            pending.Push(index); visited[index] = true;
            while (pending.TryPop(out var current))
            {
                var px = current % image.Width; var py = current / image.Width;
                left = Math.Min(left, px); right = Math.Max(right, px); top = Math.Min(top, py); bottom = Math.Max(bottom, py); size++;
                var offset = current * 3;
                // 只数晶体内部的晶面变化，不把一个平滑色块的外边框误当成晶面。
                if (px > 0 && Cyan(px - 1, py) && Math.Abs(image.Bgr[offset + 1] - image.Bgr[offset - 2]) > 45) facets++;
                if (py > 73 && Cyan(px, py - 1) && Math.Abs(image.Bgr[offset + 1] - image.Bgr[offset - image.Width * 3 + 1]) > 45) facets++;
                Visit(px - 1, py); Visit(px + 1, py); Visit(px, py - 1); Visit(px, py + 1);
            }
            var box = new ScriptRect(left, top, right - left + 1, bottom - top + 1);
            // 真实出口白光和蓝色装置同样能满足旧颜色/硬边计数，必须有落在该群内的宝石本体匹配。
            var anchors = seeds.Where(seed => Contains(Expand(box, 3), seed.Bounds.X + seed.Bounds.Width / 2,
                seed.Bounds.Y + seed.Bounds.Height / 2)).ToArray();
            if (size >= 70 && facets >= 6 && box.Width is >= 12 and <= 200 && box.Height is >= 10 and <= 100 && anchors.Length > 0)
            {
                found.RemoveAll(seed => anchors.Contains(seed));
                found.Add(new(DropKind.ColdGem, "冷冻宝石", box, anchors.Max(seed => seed.Confidence), box.Width > 45 || box.Height > 40, pickup));
            }
        }

        bool Cyan(int x, int y)
        {
            if (Excluded(x, y, player, exit) || known.Any(item => Contains(item.Bounds, x, y))) return false;
            var offset = (y * image.Width + x) * 3;
            var b = image.Bgr[offset]; var g = image.Bgr[offset + 1]; var r = image.Bgr[offset + 2];
            return g >= 135 && b >= 115 && g - r >= 75 && b - r >= 60 && Math.Abs(g - b) <= 95;
        }
        void Visit(int x, int y)
        {
            if (x < 0 || x >= image.Width || y < 73 || y >= 490) return;
            var index = y * image.Width + x;
            if (visited[index]) return;
            visited[index] = true;
            if (Cyan(x, y)) pending.Push(index);
        }
    }

    /// <summary>局部颜色相关同时保留轮廓、眼睛与晶面纹理；纯色光团即使颜色相近也不能匹配。</summary>
    private static double Score(ScriptPixels image, int x, int y, Pattern pattern)
    {
        double sum = 0, square = 0, product = 0;
        foreach (var point in pattern.Pixels)
        {
            var at = ((y + point.Y) * image.Width + x + point.X) * 3;
            for (var color = 0; color < 3; color++)
            {
                var value = image.Bgr[at + color];
                var expected = color == 0 ? point.B : color == 1 ? point.G : point.R;
                sum += value; square += value * value; product += value * (expected - pattern.Mean);
            }
        }
        var energy = square - sum * sum / (pattern.Pixels.Length * 3);
        return energy <= 1 ? 0 : product / Math.Sqrt(energy * pattern.Energy);
    }

    /// <summary>只解码所选关卡的图案；抗锯齿、遮罩和采样规则与原分类保持一致。</summary>
    private static Pattern BuildPattern(Bitmap original, double scale, Item item, Template template, bool pickup)
    {
        using var bitmap = new Bitmap(original, new Size((int)Math.Round(original.Width * scale), (int)Math.Round(original.Height * scale)));
        var pixels = new List<Pixel>();
        for (var y = 0; y < bitmap.Height; y++) for (var x = 0; x < bitmap.Width; x++)
        {
            var value = bitmap.GetPixel(x, y);
            if (value.A < 240) continue;
            // 背包背景不作为图案；战场图标已用透明遮罩排除相邻物品和品质光芒。
            var high = Math.Max(value.R, Math.Max(value.G, value.B)); var low = Math.Min(value.R, Math.Min(value.G, value.B));
            if (!template.Masked && (high < 125 || high - low < 50 && high < 200)) continue;
            pixels.Add(new(x, y, value.B, value.G, value.R));
        }
        var sampled = pixels.Where((_, index) => index % Math.Max(1, pixels.Count / 90) == 0).ToArray();
        if (sampled.Length < 3) throw new InvalidDataException($"{item.Name}的图案没有足够的可见像素。");
        var mean = sampled.Sum(value => (double)value.B + value.G + value.R) / (sampled.Length * 3);
        var energy = sampled.Sum(value => Math.Pow(value.B - mean, 2) + Math.Pow(value.G - mean, 2) + Math.Pow(value.R - mean, 2));
        var anchor = sampled.MaxBy(value => Math.Max(value.R, Math.Max(value.G, value.B)) - Math.Min(value.R, Math.Min(value.G, value.B)))!;
        return new(item.Kind, item.Name, pickup, template.Threshold, bitmap.Width, bitmap.Height, sampled, anchor, mean, energy);
    }

    // 相邻掉落允许外框相交；仅中心基本重合的多尺寸/多位置匹配才是同一个物品。
    private static bool SameItem(ScriptRect a, ScriptRect b)
        => Math.Abs(a.X + a.Width / 2 - b.X - b.Width / 2) <= Math.Max(8, Math.Min(a.Width, b.Width) / 3)
        && Math.Abs(a.Y + a.Height / 2 - b.Y - b.Height / 2) <= Math.Max(8, Math.Min(a.Height, b.Height) / 3);

    private static bool Excluded(int x, int y, ScriptRect? player, ScriptRect? exit, int playerMargin = 55)
        => player is { } hero && Contains(Expand(hero, playerMargin), x, y) || exit is { } arrow && Contains(Expand(arrow, 10), x, y);
    private static bool Contains(ScriptRect box, int x, int y) => x >= box.X && x < box.X + box.Width && y >= box.Y && y < box.Y + box.Height;
    private static ScriptRect Expand(ScriptRect box, int amount) => new(box.X - amount, box.Y - amount, box.Width + amount * 2, box.Height + amount * 2);
}
