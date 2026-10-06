using System.Text.Json;
using BqtjLauncher.Domain;

namespace MaaBackgroundProbe;

/// <summary>合成文字图片验证中文、进度数字与缓存更新；不接触游戏或发送输入。</summary>
internal static class OcrRegression
{
    public static int Run(string output, string automationDirectory)
    {
        var passed = false;
        string? error = null;
        try
        {
            GreenVisionRegression.Run();
            RequireInvalidModelsRejected(output);
            using var recognizer = MaaOcrRecognizer.CreateAsync(automationDirectory, () => { }).GetAwaiter().GetResult();
            using var frame = Draw("日常操作复制 3/22", "保存存档");
            var lines = recognizer.ReadAsync(frame, () => { }).GetAwaiter().GetResult();
            RequireText(lines, "日常操作复制3/22");
            if (DailyScriptProgressParser.Parse(lines.Select(line => line.Text)) != new DailyScriptProgress(3, 22))
                throw new InvalidOperationException("OCR输出无法解析为唯一脚本进度。");
            RequireText(lines, "保存存档");
            // 保存行后方白色数值模拟游戏侧栏透明背景，只保留绿色后应恢复独立文字。
            using var polluted = new Bitmap(320, 120);
            using (var graphics = Graphics.FromImage(polluted))
            using (var font = new Font("Microsoft YaHei", 24, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                graphics.Clear(Color.Black);
                graphics.DrawString("保存存档", font, Brushes.Lime, 40, 80);
                graphics.DrawString("78477", font, Brushes.White, 145, 80);
                graphics.DrawLines(Pens.Lime, [new Point(22, 90), new Point(25, 94), new Point(31, 82)]);
            }
            using var green = GreenScriptVision.CreateOcrImage(polluted);
            var greenLines = recognizer.ReadAsync(green, () => { }).GetAwaiter().GetResult();
            if (GreenScriptVision.FindFinalSave(polluted, greenLines, out var diagnostic) is null)
                throw new InvalidOperationException("背景过滤后的保存文字与独立绿勾未匹配：" + diagnostic);
            var rawPolluted = recognizer.ReadAsync(polluted, () => { }).GetAwaiter().GetResult();
            var saveRegion = GreenScriptVision.LocateSaveRegion(polluted.Size, rawPolluted)
                ?? throw new InvalidOperationException("无法从背景混读中定位唯一保存行。");
            using var saveFrame = polluted.Clone(saveRegion, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using var localGreen = GreenScriptVision.CreateOcrImage(saveFrame);
            var localLines = recognizer.ReadAsync(localGreen, () => { }).GetAwaiter().GetResult();
            if (GreenScriptVision.FindFinalSave(saveFrame, localLines) is null)
                throw new InvalidOperationException("自动定位局部保存行后未匹配独立绿勾。");
            using var noCheck = new Bitmap(polluted);
            using (var graphics = Graphics.FromImage(noCheck)) graphics.FillRectangle(Brushes.Black, 20, 80, 16, 24);
            using var noCheckGreen = GreenScriptVision.CreateOcrImage(noCheck);
            var noCheckLines = recognizer.ReadAsync(noCheckGreen, () => { }).GetAwaiter().GetResult();
            if (GreenScriptVision.FindFinalSave(noCheck, noCheckLines) is not null)
                throw new InvalidOperationException("绿色保存汉字笔画被误认成独立勾号。");
            using var next = Draw("日常操作复制 35/35", "保存存档");
            lines = recognizer.ReadAsync(next, () => { }).GetAwaiter().GetResult();
            RequireText(lines, "日常操作复制35/35");
            if (DailyScriptProgressParser.Parse(lines.Select(line => line.Text)) != new DailyScriptProgress(35, 35))
                throw new InvalidOperationException("新帧进度解析不符。");
            if (lines.Any(line => Compact(line.Text).Contains("3/22", StringComparison.Ordinal)))
                throw new InvalidOperationException("OCR复用了上一帧进度。");
            using var empty = Draw("", "");
            if (recognizer.ReadAsync(empty, () => { }).GetAwaiter().GetResult().Count != 0)
                throw new InvalidOperationException("空白画面残留文字结果。");
            // 并行实例各自持有模型与缓存，不能返回另一账号的进度。
            using var other = MaaOcrRecognizer.CreateAsync(automationDirectory, () => { }).GetAwaiter().GetResult();
            var parallel = Task.WhenAll(recognizer.ReadAsync(frame, () => { }), other.ReadAsync(next, () => { }))
                .GetAwaiter().GetResult();
            if (DailyScriptProgressParser.Parse(parallel[0].Select(line => line.Text)) != new DailyScriptProgress(3, 22)
                || DailyScriptProgressParser.Parse(parallel[1].Select(line => line.Text)) != new DailyScriptProgress(35, 35))
                throw new InvalidOperationException("并行OCR实例串用了另一份图片进度。");
            passed = true;
        }
        catch (Exception ex) { error = ex.Message; }
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        {
            passed,
            input = "None",
            recognition = "OCR",
            error,
            scope = "Synthetic text images only; no game capture, no final-save confirmation.",
        }));
        return passed ? 0 : 1;
    }

    private static Bitmap Draw(string header, string save)
    {
        var bitmap = new Bitmap(640, 160);
        using var graphics = Graphics.FromImage(bitmap);
        using var font = new Font("Microsoft YaHei", 24, FontStyle.Regular, GraphicsUnit.Pixel);
        graphics.Clear(Color.Black);
        graphics.DrawString(header, font, Brushes.Lime, 20, 20);
        graphics.DrawString(save, font, Brushes.Lime, 20, 80);
        return bitmap;
    }

    private static string Compact(string text) => string.Concat(text.Where(character => !char.IsWhiteSpace(character)));

    /// <summary>虚构缺失/损坏文件必须在加载原生模型前拒绝，不改动已准备的真实组件。</summary>
    private static void RequireInvalidModelsRejected(string output)
    {
        var fakeRoot = Path.Combine(output, "invalid-models");
        try
        {
            using var unexpected = MaaOcrRecognizer.CreateAsync(fakeRoot, () => { }).GetAwaiter().GetResult();
            throw new InvalidOperationException("缺失模型未被拒绝。");
        }
        catch (DirectoryNotFoundException) { }
        var fakeModels = Path.Combine(fakeRoot, "ppocr-v4-zh-cn", "model", "ocr");
        Directory.CreateDirectory(fakeModels);
        foreach (var name in new[] { "det.onnx", "keys.txt", "rec.onnx", "README.md" })
            File.WriteAllText(Path.Combine(fakeModels, name), "synthetic invalid model");
        var rejected = false;
        try { using var unexpected = MaaOcrRecognizer.CreateAsync(fakeRoot, () => { }).GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("损坏模型未被拒绝。");
    }

    private static void RequireText(IReadOnlyList<OcrLine> lines, string expected)
    {
        if (!lines.Any(line => Compact(line.Text) == expected))
            throw new InvalidOperationException("合成文字未正确识别：" + expected);
    }
}
