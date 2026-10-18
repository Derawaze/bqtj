using System.Text;
using System.Text.RegularExpressions;
using BqtjLauncher.Application;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>定位存档名/等级一行并规范等级后缀；数字开头、带“存档”字样的合法名称均保留。</summary>
internal static partial class SaveSlotNameReader
{
    /// <summary>相对卡片中心截取名称行，避开左侧菱形与下方时间；右上灰色通关标记由黄色遮罩排除。</summary>
    public static ScriptRect NameRegion(Point center) => new(center.X - 76, center.Y - 17, 168, 20);

    /// <summary>黄色OCR可能把同一行拆成多个片段，按横向位置组合；只修正行尾等级，不改用户名正文。</summary>
    public static string Parse(IReadOnlyList<ScriptText> lines)
    {
        var text = string.Join(" ", lines.OrderBy(line => line.Bounds.X).Select(line => line.Text.Trim()).Where(value => value.Length > 0))
            .Normalize(NormalizationForm.FormKC).Trim();
        if (text.Length == 0) return "未识别名称";
        var level = LevelSuffix().Match(text);
        if (!level.Success) return text;
        var name = text[..level.Index].TrimEnd();
        return (name.Length == 0 ? "未识别名称" : name) + " lv." + level.Groups[1].Value;
    }

    [GeneratedRegex(@"[lI1|]v\s*\.?\s*([0-9]{1,3})\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LevelSuffix();
}
