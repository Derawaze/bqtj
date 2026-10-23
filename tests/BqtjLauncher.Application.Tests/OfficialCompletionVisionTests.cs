using System.IO;
using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

/// <summary>整体完成依据标题左侧独立绿勾，排除OCR装饰、其他行的绿勾及错误形状。</summary>
public sealed class OfficialCompletionVisionTests
{
    [Fact]
    public void CustomOfficialTitleStillRequiresTheSameGreenCheckGeometry()
    {
        using var image = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Fixtures", "official-completed-header.png"));
        OcrLine[] lines = [new("√我的日常23/23", new(27, 16, 122, 17), .95)];
        Assert.NotNull(GreenScriptVision.FindCompletedHeader(image, lines, "我的日常"));
        Assert.Null(GreenScriptVision.FindCompletedHeader(image, lines, "日常操作复制"));
    }
    [Theory]
    [InlineData(27, 16, 122, 17)]
    [InlineData(33, 15, 116, 17)]
    public void ActualCompletedHeaderWorksWithBothObservedOcrBounds(int x, int y, int width, int height)
    {
        // 只保留公共脚本标题和勾号，不包含账号名或用户完整画面。
        using var image = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Fixtures", "official-completed-header.png"));
        Assert.NotNull(GreenScriptVision.FindCompletedHeader(image,
            [new("√日常操作复制23/23", new(x, y, width, height), .95)]));
    }
    [Theory]
    [InlineData("check", true)]
    [InlineData("slash", false)]
    [InlineData("cross", false)]
    [InlineData("box", false)]
    [InlineData("line", false)]
    [InlineData("other-row", false)]
    [InlineData("red-check", false)]
    [InlineData("none", false)]
    public void RequiresGreenCheckBesideCompletedHeader(string shape, bool expected)
    {
        using var image = new Bitmap(182, 420);
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(Color.Black);
        using var pen = new Pen(shape == "red-check" ? Color.Red : Color.Lime);
        switch (shape)
        {
            case "check":
            case "red-check":
                graphics.DrawLines(pen, [new Point(29, 24), new Point(32, 28), new Point(38, 16)]); break;
            case "other-row":
                graphics.DrawLines(pen, [new Point(29, 44), new Point(32, 48), new Point(38, 36)]); break;
            case "slash": graphics.DrawLine(pen, 29, 28, 38, 16); break;
            case "cross":
                graphics.DrawLine(pen, 29, 28, 38, 16); graphics.DrawLine(pen, 29, 16, 38, 28); break;
            case "box": graphics.FillRectangle(Brushes.Lime, 29, 16, 10, 13); break;
            case "line": graphics.DrawLine(pen, 29, 24, 38, 24); break;
        }
        Assert.Equal(expected, GreenScriptVision.FindCompletedHeader(image,
            [new("√日常操作复制23/23", new(27, 16, 122, 17), .99)]) is not null);
        // 22/23冷却等待仍未完成，绿色形状不能提前触发。
        Assert.Null(GreenScriptVision.FindCompletedHeader(image,
            [new("日常操作复制22/23", new(27, 16, 122, 17), .99)]));
    }
}
