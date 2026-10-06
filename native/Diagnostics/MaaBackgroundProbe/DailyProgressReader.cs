using System.Drawing.Imaging;
using System.Text;
using BqtjLauncher.Domain;

namespace MaaBackgroundProbe;

/// <summary>同帧识别结果；原始文字仅供人工探针诊断，不传给面板或写入日志。</summary>
internal sealed record DailyProgressScene(DailyScriptProgress? Progress, bool FinalSaveChecked,
    bool HasFailedItems, string GreenText, string Diagnostic, int TitleCandidates);

/// <summary>探针与执行器共用侧栏识别，避免实机校准成功而自动流程使用另一组参数。</summary>
internal static class DailyProgressReader
{
    // 950×600实机校准区域：排除左侧设置窗口与底部数值，保留标题、完整进度及最后保存行。
    // 扩大到整个右侧会改变OCR结果，实机同帧出现进度无法解析，不能靠放宽解析补救。
    private static readonly Rectangle CalibratedRegion = new(759, 154, 182, 420);

    /// <summary>原图定位保存行，再以局部绿色文字和独立勾形确认；图片在本次读取后释放。</summary>
    public static async Task<DailyProgressScene> ReadAsync(Bitmap frame, MaaOcrRecognizer recognizer,
        Action assertValid, Rectangle? diagnosticRegion = null)
    {
        if (frame.Size != new Size(950, 600))
            throw new InvalidOperationException("日常进度识别仅支持950×600。");
        using var cropped = frame.Clone(diagnosticRegion ?? CalibratedRegion, PixelFormat.Format24bppRgb);
        var lines = await recognizer.ReadAsync(cropped, assertValid);
        var saveRegion = GreenScriptVision.LocateSaveRegion(cropped.Size, lines);
        IReadOnlyList<OcrLine> greenLines = [];
        FinalSaveVisualEvidence? saveEvidence = null;
        var saveDiagnostic = "原图未定位到唯一保存行，不猜测位置";
        if (saveRegion is Rectangle row)
        {
            using var saveFrame = cropped.Clone(row, PixelFormat.Format24bppRgb);
            using var greenImage = GreenScriptVision.CreateOcrImage(saveFrame);
            greenLines = await recognizer.ReadAsync(greenImage, assertValid);
            saveEvidence = GreenScriptVision.FindFinalSave(saveFrame, greenLines, out saveDiagnostic);
        }
        assertValid();
        var progress = DailyScriptProgressParser.Parse(lines.Select(line => line.Text));
        var failed = lines.Any(line => line.Text.TrimStart().StartsWith('×'));
        var titleCandidates = lines.Count(line => new string(line.Text.Normalize(NormalizationForm.FormKC)
            .Where(c => !char.IsWhiteSpace(c)).ToArray()).Contains("日常操作复制", StringComparison.Ordinal));
        return new(progress, saveEvidence is not null, failed,
            string.Join("；", greenLines.Select(line => line.Text)), saveDiagnostic, titleCandidates);
    }
}
