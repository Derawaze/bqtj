namespace BqtjLauncher.Domain.Tests;

/// <summary>覆盖OCR数字边界与歧义，避免误读菜单文字或进度后提前关闭游戏。</summary>
public sealed class DailyScriptProgressParserTests
{
    [Theory]
    [InlineData("日常操作复制 3/22", 3, 22)]
    [InlineData("✓ 日 常 操 作 复 制 ３ ／ ２２", 3, 22)]
    [InlineData("日常操作复制 0/35", 0, 35)]
    [InlineData("✔日常操作复制 35/35", 35, 35)]
    [InlineData("/日常操作复制22/22", 22, 22)]
    public void ReadsOnlyValidUniqueHeader(string text, int completed, int total)
        => Assert.Equal(new DailyScriptProgress(completed, total), DailyScriptProgressParser.Parse(["保存存档", text]));

    [Theory]
    [InlineData("日常操作复制 23/22")]
    [InlineData("日常操作复制 0/0")]
    [InlineData("日常操作复制 2/2147483648")]
    [InlineData("日常操作复制 2O/22")]
    [InlineData("日常操作复制 22//22")]
    [InlineData("日常操作复制 -1/22")]
    [InlineData("日常操作复制 22/22 保存存档")]
    [InlineData("日常操作复制")]
    [InlineData("其他脚本 22/22")]
    public void RejectsInvalidOrIncompleteText(string text) => Assert.Null(DailyScriptProgressParser.Parse([text]));

    [Fact]
    public void DuplicateHeaderIsAmbiguousEvenWithSameProgress()
        => Assert.Null(DailyScriptProgressParser.Parse(["日常操作复制22/22", "日常操作复制22/22"]));

    [Fact]
    public void DoesNotCombineFragmentsFromDifferentLines()
        => Assert.Null(DailyScriptProgressParser.Parse(["日常操作复制", "22/22"]));
}
