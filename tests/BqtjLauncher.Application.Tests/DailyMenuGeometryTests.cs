using System.Drawing;
using MaaBackgroundProbe;

namespace BqtjLauncher.Application.Tests;

/// <summary>虚构卡片验证槽位顺序及布局拒绝，不读取真实存档画面。</summary>
public sealed class DailyMenuGeometryTests
{
    [Theory]
    [InlineData(1, 371, 228)]
    [InlineData(5, 371, 350)]
    [InlineData(8, 578, 411)]
    public void SelectsRowMajorSlotOnlyWithAllEightBorders(int number, int x, int y)
    {
        using var frame = Cards();
        Assert.Equal(new Point(x, y), DailyMenuGeometry.FindSaveSlot(frame, new Rectangle(440, 168, 70, 14), number));
    }

    [Fact]
    public void RejectsMissingCardEvenWhenSelectedCardExists()
    {
        using var frame = Cards();
        using var graphics = Graphics.FromImage(frame);
        graphics.FillRectangle(Brushes.Black, 474, 384, 210, 65);
        Assert.Throws<InvalidOperationException>(() => DailyMenuGeometry.FindSaveSlot(frame, new Rectangle(440, 168, 70, 14), 1));
    }

    [Fact]
    public void RejectsUniformGrayAndWrongTitleAnchor()
    {
        using var frame = new Bitmap(950, 600);
        using (var graphics = Graphics.FromImage(frame)) graphics.Clear(Color.FromArgb(70, 70, 70));
        Assert.Throws<InvalidOperationException>(() => DailyMenuGeometry.FindSaveSlot(frame, new Rectangle(440, 168, 70, 14), 5));
        using var cards = Cards();
        Assert.Throws<InvalidOperationException>(() => DailyMenuGeometry.FindSaveSlot(cards, new Rectangle(440, 198, 70, 14), 5));
    }

    [Theory]
    [InlineData("读取存档", true)]
    [InlineData("选择存档", false)]
    [InlineData("保存存档", false)]
    [InlineData("", false)]
    public void RecognizesActualDialogTitleWithoutAcceptingOtherMenus(string text, bool expected)
        => Assert.Equal(expected, DailyMenuGeometry.IsSaveDialogTitle(text));
    private static Bitmap Cards()
    {
        var frame = new Bitmap(950, 600);
        using var graphics = Graphics.FromImage(frame);
        graphics.Clear(Color.Black);
        using var pen = new Pen(Color.FromArgb(80, 80, 80));
        for (var row = 0; row < 4; row++)
            for (var column = 0; column < 2; column++)
                graphics.DrawRectangle(pen, column == 0 ? 271 : 478, 201 + row * 61, 199, 54);
        return frame;
    }
}
