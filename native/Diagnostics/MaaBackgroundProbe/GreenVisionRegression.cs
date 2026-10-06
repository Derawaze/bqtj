namespace MaaBackgroundProbe;

/// <summary>纯合成像素检验保存行勾形，防止颜色正确但形状错误造成误完成。</summary>
internal static class GreenVisionRegression
{
    public static void Run()
    {
        var line = new OcrLine("保存存档", new Rectangle(120, 240, 288, 72), 0.99);
        using var frame = new Bitmap(240, 120);
        using var graphics = Graphics.FromImage(frame);
        using var green = new Pen(Color.Lime);
        using var red = new Pen(Color.Red);
        void Clear() => graphics.Clear(Color.Black);
        void Check(int x, Pen pen) => graphics.DrawLines(pen, [new Point(x, 90), new Point(x + 3, 94), new Point(x + 9, 82)]);
        void Require(bool expected, string scene)
        {
            if ((GreenScriptVision.FindFinalSave(frame, [line]) is not null) != expected)
                throw new InvalidOperationException("保存行勾形判定不符：" + scene);
        }
        Clear(); Check(22, green); Require(true, "绿色勾形");
        if (GreenScriptVision.FindFinalSave(frame, [line with { Bounds = new Rectangle(66, 240, 342, 72) }]) is null)
            throw new InvalidOperationException("OCR框包含但文字省略勾号时未重新检查笔画。");
        Clear(); graphics.DrawLines(green, [new Point(22, 91), new Point(24, 89), new Point(26, 92), new Point(31, 82)]);
        Require(true, "小字号左端衬线勾形");
        Clear(); Check(22, red); Require(false, "红色勾形");
        Clear(); Require(false, "缺失勾形");
        graphics.DrawLine(green, 22, 94, 31, 82); Require(false, "绿色单斜杠");
        Clear(); graphics.DrawLine(green, 22, 83, 31, 94); graphics.DrawLine(green, 22, 94, 31, 83); Require(false, "绿色叉号");
        Clear(); graphics.FillRectangle(Brushes.Lime, 22, 82, 6, 13); Require(false, "绿色色块");
        Clear(); Check(6, green); Check(22, green); Require(false, "重复勾形");
        Clear(); Check(22, green);
        if (GreenScriptVision.FindFinalSave(frame, [line, line]) is not null)
            throw new InvalidOperationException("重复保存文字未被拒绝。");
        if (GreenScriptVision.FindFinalSave(frame, [line with { Text = "保存存档78477万" }]) is not null)
            throw new InvalidOperationException("背景数值混读未被拒绝。");
        var raw = line with { Text = "保存存档78477万", Bounds = new Rectangle(40, 80, 120, 24) };
        if (GreenScriptVision.LocateSaveRegion(frame.Size, [raw]) is null
            || GreenScriptVision.LocateSaveRegion(frame.Size, [raw, raw]) is not null)
            throw new InvalidOperationException("唯一保存行定位或重复拒绝失败。");
        graphics.FillRectangle(Brushes.White, 160, 85, 10, 12);
        graphics.FillRectangle(Brushes.Gold, 180, 85, 10, 12);
        using var mask = GreenScriptVision.CreateOcrImage(frame);
        if (mask.GetPixel(162 * 3, 90 * 3).ToArgb() != Color.Black.ToArgb()
            || mask.GetPixel(182 * 3, 90 * 3).ToArgb() != Color.Black.ToArgb())
            throw new InvalidOperationException("白色或黄色背景数值未被过滤。");
    }
}
