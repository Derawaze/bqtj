using System.Text.Json;

namespace MaaBackgroundProbe;

/// <summary>合成图片识别回归：位置变化必须重定位，缺失或重复目标必须拒绝。</summary>
internal static class TemplateRegression
{
    public static int Run(string output)
    {
        string? error = null;
        var passed = false;
        try
        {
            using var template = new Bitmap(24, 24);
            using (var graphics = Graphics.FromImage(template))
            {
                graphics.Clear(Color.Navy);
                graphics.FillEllipse(Brushes.Gold, 2, 2, 18, 18);
                graphics.FillRectangle(Brushes.Red, 9, 6, 4, 14);
                graphics.DrawLine(Pens.White, 0, 0, 23, 23);
            }
            using var recognizer = new MaaTemplateRecognizer();
            using var frame = new Bitmap(320, 200);
            using var graphicsFrame = Graphics.FromImage(frame);
            graphicsFrame.Clear(Color.DimGray);
            graphicsFrame.DrawImageUnscaled(template, 130, 70);
            var match = recognizer.FindUniqueAsync(frame, template, () => { }).GetAwaiter().GetResult();
            if (match != new Rectangle(130, 70, 24, 24)) throw new InvalidOperationException("模板定位坐标不符。");
            graphicsFrame.Clear(Color.DimGray);
            graphicsFrame.DrawImageUnscaled(template, 220, 120);
            match = recognizer.FindUniqueAsync(frame, template, () => { }).GetAwaiter().GetResult();
            if (match != new Rectangle(220, 120, 24, 24)) throw new InvalidOperationException("位置改变后仍使用旧坐标。");
            graphicsFrame.DrawImageUnscaled(template, 20, 20);
            RequireRejected(recognizer, frame, template, "重复目标");
            graphicsFrame.Clear(Color.DimGray);
            RequireRejected(recognizer, frame, template, "目标缺失");
            // 两个识别器并行处理不同帧，验证资源和结果不共享，不能把另一账号的坐标用于本账号。
            using var other = new MaaTemplateRecognizer();
            using var otherFrame = new Bitmap(320, 200);
            using var otherGraphics = Graphics.FromImage(otherFrame);
            graphicsFrame.DrawImageUnscaled(template, 60, 80);
            otherGraphics.Clear(Color.DimGray);
            otherGraphics.DrawImageUnscaled(template, 210, 110);
            var results = Task.WhenAll(recognizer.FindUniqueAsync(frame, template, () => { }),
                other.FindUniqueAsync(otherFrame, template, () => { })).GetAwaiter().GetResult();
            if (results[0] != new Rectangle(60, 80, 24, 24) || results[1] != new Rectangle(210, 110, 24, 24))
                throw new InvalidOperationException("并行识别串用了另一个实例的坐标。");
            passed = true;
        }
        catch (Exception ex) { error = ex.Message; }
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        {
            passed,
            input = "None",
            recognition = "TemplateMatch",
            error,
            scope = "Synthetic images only; no window input.",
        }));
        return passed ? 0 : 1;
    }

    private static void RequireRejected(MaaTemplateRecognizer recognizer, Image frame, Image template, string label)
    {
        try { recognizer.FindUniqueAsync(frame, template, () => { }).GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(label + "未被拒绝。");
    }
}
