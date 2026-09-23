using System.Text;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>按 CommandLineToArgvW 兼容规则编码 CreateProcessW 命令行。</summary>
internal static class WindowsCommandLine
{
    public static string Build(string executablePath, IEnumerable<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        return string.Join(' ', new[] { executablePath }.Concat(arguments).Select(QuoteArgument));
    }

    internal static string QuoteArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length > 0
            && !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        var result = new StringBuilder(argument.Length + 2).Append('"');
        var slashCount = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                slashCount++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', slashCount * 2 + 1).Append('"');
                slashCount = 0;
                continue;
            }

            result.Append('\\', slashCount).Append(character);
            slashCount = 0;
        }

        // 结束引号前的反斜杠必须翻倍，否则会转义结束引号。
        return result.Append('\\', slashCount * 2).Append('"').ToString();
    }
}
