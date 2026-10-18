using System.Text.Json;
using BqtjLauncher.Runtime.Flash;

/// <summary>只读回放已授权的侧栏图片与OCR；不启动游戏、不连接账号库、不发送点击。</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        using var frame = new Bitmap(args[0]);
        using var json = JsonDocument.Parse(File.ReadAllText(args[1]));
        var lines = json.RootElement.GetProperty("filtered").EnumerateArray().Select(line =>
        {
            var box = line.GetProperty("box");
            return new OcrLine(line.GetProperty("text").GetString() ?? "",
                new Rectangle(box[0].GetInt32(), box[1].GetInt32(), box[2].GetInt32(), box[3].GetInt32()),
                line.GetProperty("score").GetDouble());
        }).ToArray();
        var evidence = GreenScriptVision.FindCompletedHeader(frame, lines, out var diagnostic);
        Console.WriteLine(diagnostic);
        Console.WriteLine(evidence is null ? "FAIL: 未识别整体完成绿勾" : $"PASS: N/N标题左侧绿色勾 {evidence.Checkmark}");
        return evidence is null ? 1 : 0;
    }
}
