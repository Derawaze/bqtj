using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BqtjLauncher.Domain;

/// <summary>已校验的官方脚本进度；只表示文字数值，不包含开始或保存成功结论。</summary>
public sealed record DailyScriptProgress(int Completed, int Total);

/// <summary>从指定侧栏的OCR行读取唯一完整标题；不拼接残缺行或猜测模糊数字。</summary>
public static partial class DailyScriptProgressParser
{
    /// <summary>支持空白及全角字符；标题冲突、缺失、溢出或非法进度返回空值。</summary>
    public static DailyScriptProgress? Parse(IEnumerable<string> lines, string? scriptName = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var names = scriptName is null ? new[] { "日常操作复制", "日常操作" } : [Compact(scriptName)];
        var pattern = new Regex(@"\A[✓✔√/]?(?:" + string.Join("|", names.Select(Regex.Escape)) + @")([0-9]+)/([0-9]+)\z", RegexOptions.CultureInvariant);
        string? header = null;
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line) || line.Length > 256) continue;
            var compact = Compact(line);
            if (!names.Any(name => compact.Contains(name, StringComparison.Ordinal))) continue;
            // 即使两个标题内容相同，也可能混入另一处脚本菜单，不能任选一个用于关闭判断。
            if (header is not null) return null;
            header = compact;
        }
        if (header is null) return null;
        var match = pattern.Match(header);
        if (!match.Success
            || !int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var completed)
            || !int.TryParse(match.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var total)
            || total <= 0 || completed > total) return null;
        return new DailyScriptProgress(completed, total);
    }

    // 实机OCR曾将标题勾形装饰读成斜杠；只允许在标题前出现，完成绿勾另由同帧像素确认。
    // 数字部分仍精确解析，不把O、l等猜成数字。
    private static string Compact(string text)
        => string.Concat(text.Normalize(NormalizationForm.FormKC).Where(character => !char.IsWhiteSpace(character)));
}
