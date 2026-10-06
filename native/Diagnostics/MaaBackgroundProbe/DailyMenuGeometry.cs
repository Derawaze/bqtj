namespace MaaBackgroundProbe;

/// <summary>从已识别存档标题确定固定游戏布局；八个卡片边框不符则拒绝选档。</summary>
internal static class DailyMenuGeometry
{
    /// <summary>只匹配弹窗顶部的固定标题；首页同名按钮由不同区域识别。</summary>
    public static bool IsSaveDialogTitle(string text) => text == "读取存档";

    public static Point FindSaveSlot(Bitmap frame, Rectangle title, int saveNumber)
    {
        if (frame.Size != new Size(950, 600) || saveNumber is < 1 or > 8)
            throw new InvalidOperationException();
        var center = title.Left + title.Width / 2;
        var top = title.Top + title.Height / 2;
        var cards = new List<Rectangle>();
        for (var row = 0; row < 4; row++)
            for (var column = 0; column < 2; column++)
            {
                // 游戏卡片固定两列四行；坐标相对本次标题，不依赖桌面位置或WPF缩放。
                var card = new Rectangle(center + (column == 0 ? -204 : 3), top + 26 + row * 61, 200, 55);
                if (!new Rectangle(Point.Empty, frame.Size).Contains(card) || !HasCardEdges(frame, card))
                    throw new InvalidOperationException("存档卡片布局未匹配。");
                cards.Add(card);
            }
        var selected = cards[saveNumber - 1];
        return new Point(selected.Left + selected.Width / 2, selected.Top + selected.Height / 2);
    }

    /// <summary>在预期上下边附近寻找贯穿卡片的灰色边线；不读取存档名称和时间。</summary>
    private static bool HasCardEdges(Bitmap frame, Rectangle card)
    {
        bool Edge(int expected)
        {
            for (var y = expected - 4; y <= expected + 4; y++)
            {
                var hits = 0;
                for (var x = card.Left + 8; x < card.Right - 8; x += 4)
                {
                    var color = frame.GetPixel(x, y);
                    var above = frame.GetPixel(x, y - 2);
                    var below = frame.GetPixel(x, y + 2);
                    // 必须是边线而非整块灰色：至少一侧存在明显亮度跃变。
                    if (color.R is >= 40 and <= 115 && Math.Abs(color.R - color.G) < 8 && Math.Abs(color.R - color.B) < 8
                        && Math.Max(Math.Abs(color.R - above.R), Math.Abs(color.R - below.R)) >= 12) hits++;
                }
                if (hits >= 40) return true;
            }
            return false;
        }
        return Edge(card.Top) && Edge(card.Bottom - 1);
    }
}
