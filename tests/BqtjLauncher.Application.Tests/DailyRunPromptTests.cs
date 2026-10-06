using MaaBackgroundProbe;

namespace BqtjLauncher.Application.Tests;

/// <summary>重放确认文案末尾标点被OCR遗漏的情况；其他脚本及非运行提示仍拒绝。</summary>
public sealed class DailyRunPromptTests
{
    [Theory]
    [InlineData("是否开始运行 日常操作复制", true)]
    [InlineData("是否开始运行日常操作复制？", true)]
    [InlineData("是否开始运行日常操作复制?", true)]
    [InlineData("是否开始运行日常操作复制！", true)]
    [InlineData("是否开始运行《日常操作复制》", true)]
    [InlineData("是否开始运行“日常操作复制”？", true)]
    [InlineData("是否开始运行其他脚本？", false)]
    [InlineData("是否开始运行日常操作复制测试？", false)]
    [InlineData("是否删除日常操作复制？", false)]
    [InlineData("日常操作复制", false)]
    [InlineData("", false)]
    public void MatchesNamedRunPromptEvenWhenFinalPunctuationIsMissing(string text, bool expected)
        => Assert.Equal(expected, DailyRunPrompt.IsExpected(text));
}