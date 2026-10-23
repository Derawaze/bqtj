using System.Text;
using System.Text.RegularExpressions;

namespace BqtjLauncher.Domain;

/// <summary>一条已定位到掉落查看弹窗的OCR结果；绿色由同帧像素识别提供。</summary>
public sealed record MaterialQuotaLine(string Text, bool IsGreen);

/// <summary>一组共享材料限额。只有有效计数且计数显示绿色，才作为刷满证据。</summary>
public sealed record MaterialQuota(int Collected, int Limit, bool IsGreen)
{
    public bool IsFull => Limit > 0 && Collected >= Limit && IsGreen;
}

/// <summary>天空顶的两项独立目标；冷冻宝石/守护宝石合并计数，冥王图鉴单独计数。</summary>
public sealed record DailyMaterialQuota(MaterialQuota Gems, MaterialQuota Codex)
{
    public bool IsFull => Gems.IsFull && Codex.IsFull;

    /// <summary>只解释同一次弹窗观察；不把不同截图、不同材料的数字或旧绿色缓存拼起来。</summary>
    public static DailyMaterialQuota? Parse(IReadOnlyList<MaterialQuotaLine> lines)
    {
        MaterialQuota? gems = null, codex = null;
        foreach (var line in lines)
        {
            var normalized = line.Text.Normalize(NormalizationForm.FormKC);
            var text = string.Concat(normalized.Where(character => !char.IsWhiteSpace(character)));
            var isGem = text.Contains("冷冻宝石", StringComparison.Ordinal) || text.Contains("守护宝石", StringComparison.Ordinal);
            var isCodex = text.Contains("冥王图鉴", StringComparison.Ordinal);
            if (!isGem && !isCodex) continue;
            // 实机将“守护宝石”单列，计数只放在冷冻宝石行；纯别名不构成另一项缺失计数。
            if (isGem && text is "冷冻宝石" or "守护宝石") continue;
            // 不能确定数字属于哪项时交还观察层重读，不根据“有绿色”直接宣布完成。
            if (isGem && isCodex) return null;
            var matches = Regex.Matches(normalized, @"(?<![\d-])(\d+)\s*/\s*(\d+)(?!\d)");
            if (matches.Count != 1 || !int.TryParse(matches[0].Groups[1].Value, out var current)
                || !int.TryParse(matches[0].Groups[2].Value, out var limit) || limit <= 0) return null;
            var quota = new MaterialQuota(current, limit, line.IsGreen);
            var previous = isGem ? gems : codex;
            if (previous is not null && previous != quota) return null;
            if (isGem) gems = quota; else codex = quota;
        }
        return gems is null || codex is null ? null : new(gems, codex);
    }
}
