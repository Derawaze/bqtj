using System.IO;
using System.Text.Json;
using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

/// <summary>存档名字与等级规范化不丢弃数字姓名，不把“存档”前缀或正文1v误改掉。</summary>
public sealed class SaveSlotNameReaderTests
{
    [Theory]
    [InlineData("123456 1v.99", "123456 lv.99")]
    [InlineData("测试角色lv.9", "测试角色 lv.9")]
    [InlineData("存档高手 Iv.12", "存档高手 lv.12")]
    [InlineData("1v.99挑战者 lv.3", "1v.99挑战者 lv.3")]
    [InlineData("测试季季 LV . 99", "测试季季 lv.99")]
    [InlineData("1v.99", "未识别名称 lv.99")]
    public void NormalizesOnlyLevelSuffix(string source, string expected)
        => Assert.Equal(expected, SaveSlotNameReader.Parse([new(source, new(0, 0, 80, 12), .99)]));

    [Fact]
    public void JoinsSplitNameAndLevelByHorizontalPosition()
        => Assert.Equal("测试名字 lv.99", SaveSlotNameReader.Parse([
            new("1v.99", new(60, 2, 30, 12), .98), new("测试名字", new(0, 0, 50, 12), .97)]));

    [Fact]
    public void EmptyTextRemainsExplicitlyUnknown() => Assert.Equal("未识别名称", SaveSlotNameReader.Parse([]));

    [Theory]
    [InlineData(.95, .96, "白底名字")]
    [InlineData(.76, .93, "黑底名字")]
    [InlineData(.96, .93, "白底名字")]
    public void ContrastVariantRequiresMaterialScoreImprovement(double lightScore, double darkScore, string expected)
    {
        var value = new
        {
            light = new { filtered = new[] { new { text = "白底名字", score = lightScore } } },
            dark = new { filtered = new[] { new { text = "黑底名字", score = darkScore } } }
        };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(value));
        var selected = SharedScriptVision.SelectSaveNameVariant(json.RootElement);
        Assert.Equal(expected, selected.GetProperty("filtered")[0].GetProperty("text").GetString());
    }
    [Fact]
    public async Task OldComponentIsRejectedBeforeStartingItsExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), "bqtj-old-component-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "BqtjScriptVision.exe"), "not-an-executable");
            await File.WriteAllTextAsync(Path.Combine(root, "component.json"), "{\"version\":1}");
            await using var vision = new SharedScriptVision(root);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => vision.BeginAsync(default));
            Assert.Contains("组件版本过旧", error.Message);
        }
        finally { Directory.Delete(root, true); }
    }
}
