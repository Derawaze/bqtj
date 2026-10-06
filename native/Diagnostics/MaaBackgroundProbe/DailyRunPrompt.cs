using System.Text;

namespace MaaBackgroundProbe;

/// <summary>确认具名日常脚本的运行提示；不靠易被OCR漏掉的句尾标点判定。</summary>
internal static class DailyRunPrompt
{
    private static readonly (char Open, char Close)[] NameQuotes = [('《', '》'), ('〈', '〉'), ('“', '”'), ('「', '」'), ('『', '』'), ('"', '"')];

    public static bool IsExpected(string text)
    {
        text = string.Concat(text.Normalize(NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c)));
        const string prefix = "是否开始运行";
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var name = text[prefix.Length..].TrimStart(':').TrimEnd('?', '!', '.', '。');
        foreach (var (open, close) in NameQuotes)
            if (name.Length >= 2 && name[0] == open && name[^1] == close)
            {
                name = name[1..^1];
                break;
            }
        // 脚本名必须完整相等，避免将带相同前缀的其他脚本误判为当前日常。
        return name == "日常操作复制";
    }
}
