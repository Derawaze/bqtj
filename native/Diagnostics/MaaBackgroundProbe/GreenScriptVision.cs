using System.Drawing.Drawing2D;
using System.Text;

namespace MaaBackgroundProbe;

/// <summary>同一帧最后保存行的画面证据，坐标属于输入的侧栏图片；不表示服务端已保存。</summary>
internal sealed record FinalSaveVisualEvidence(Rectangle Label, Rectangle Checkmark);

/// <summary>按绿色优势去除侧栏背景，检查保存行左侧的独立勾形；探针与日常执行器共用。</summary>
internal static class GreenScriptVision
{
    public const int OcrScale = 3;

    // 当前探针参数保留抗锯齿的绿色笔画，排除白色、黄色数值及暗绿色背景。
    private static bool IsGreen(Color pixel) => pixel.G >= 70 && pixel.G - pixel.R >= 40 && pixel.G - pixel.B >= 35;

    /// <summary>原图OCR只用于定位唯一保存行，背景混读不作完成证据；随后在同帧局部重新识别。</summary>
    public static Rectangle? LocateSaveRegion(Size frameSize, IReadOnlyList<OcrLine> rawLines)
    {
        var candidates = rawLines.Where(line => line.Text.Contains("保存存档", StringComparison.Ordinal)).ToArray();
        if (candidates.Length != 1) return null;
        var box = candidates[0].Bounds;
        if (box.Width <= 0 || box.Height <= 0 || !new Rectangle(Point.Empty, frameSize).Contains(box)) return null;
        // 左侧为勾号留出一字符宽度；仅扩少量上下边，防止整侧栏小字检测遗漏末行。
        return Rectangle.Intersect(Rectangle.FromLTRB(box.Left - box.Height, box.Top - 3, box.Right + 3, box.Bottom + 3),
            new Rectangle(Point.Empty, frameSize));
    }

    /// <summary>只保留绿色像素，放大供小字号OCR使用；不改变原始坐标或保存图片。</summary>
    public static Bitmap CreateOcrImage(Bitmap frame)
    {
        using var mask = new Bitmap(frame.Width, frame.Height);
        for (var y = 0; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
                mask.SetPixel(x, y, IsGreen(frame.GetPixel(x, y)) ? Color.White : Color.Black);
        var enlarged = new Bitmap(frame.Width * OcrScale, frame.Height * OcrScale);
        using var graphics = Graphics.FromImage(enlarged);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(mask, new Rectangle(Point.Empty, enlarged.Size));
        return enlarged;
    }

    /// <summary>仅接受唯一、完整的保存行；OCR勾字符不作证据，重新分析原帧绿色连通笔画。</summary>
    public static FinalSaveVisualEvidence? FindFinalSave(Bitmap frame, IReadOnlyList<OcrLine> greenLines)
        => FindFinalSave(frame, greenLines, out _);

    /// <summary>只返回形状统计供人工排查，不输出原始像素或落盘真实截图。</summary>
    public static FinalSaveVisualEvidence? FindFinalSave(Bitmap frame, IReadOnlyList<OcrLine> greenLines, out string diagnostic)
    {
        var candidates = greenLines.Where(line => WithoutDecoration(line.Text) == "保存存档").ToArray();
        diagnostic = $"保存文字候选{candidates.Length}个";
        if (candidates.Length != 1) return null;
        var line = candidates[0];
        var box = Rectangle.FromLTRB(line.Bounds.Left / OcrScale, line.Bounds.Top / OcrScale,
            (line.Bounds.Right + OcrScale - 1) / OcrScale, (line.Bounds.Bottom + OcrScale - 1) / OcrScale);
        if (box.Height < 4 || !new Rectangle(Point.Empty, frame.Size).Contains(box)) return null;
        // OCR检测框可能包含勾号但文字结果省略它，不能以输出字符决定搜索范围。
        // 向框内最多扩至一个字符高度；后续依靠独立连通笔画形状排除汉字。
        var search = Rectangle.Intersect(Rectangle.FromLTRB(Math.Max(0, box.Left - box.Height * 2), Math.Max(0, box.Top - 2),
            box.Left + box.Height, box.Bottom + 2), new Rectangle(Point.Empty, frame.Size));
        var components = Components(frame, search).ToArray();
        diagnostic += $"；文字框{box}；左侧笔画：" + string.Join("；", components.Select(Describe));
        var marks = components.Where(points => IsCheckmark(points, box.Height)).ToArray();
        if (marks.Length != 1) return null;
        return new FinalSaveVisualEvidence(box, Bounds(marks[0]));
    }

    private static string Compact(string text) => string.Concat(text.Normalize(NormalizationForm.FormKC)
        .Where(character => !char.IsWhiteSpace(character)));

    private static string WithoutDecoration(string text)
    {
        var compact = Compact(text);
        return compact.Length > 0 && "✓✔√/".Contains(compact[0], StringComparison.Ordinal) ? compact[1..] : compact;
    }

    /// <summary>使用八邻接保留细小斜线笔画；分析范围限定在当前保存文字的左侧。</summary>
    private static IEnumerable<List<Point>> Components(Bitmap frame, Rectangle region)
    {
        var visited = new HashSet<Point>();
        for (var y = region.Top; y < region.Bottom; y++)
            for (var x = region.Left; x < region.Right; x++)
            {
                var start = new Point(x, y);
                if (visited.Contains(start) || !IsGreen(frame.GetPixel(x, y))) continue;
                var queue = new Queue<Point>();
                var points = new List<Point>();
                visited.Add(start);
                queue.Enqueue(start);
                while (queue.TryDequeue(out var point))
                {
                    points.Add(point);
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var next = new Point(point.X + dx, point.Y + dy);
                            if (!region.Contains(next) || visited.Contains(next) || !IsGreen(frame.GetPixel(next.X, next.Y))) continue;
                            visited.Add(next);
                            queue.Enqueue(next);
                        }
                }
                yield return points;
            }
    }

    /// <summary>勾形应先短下行再长上行；拒绝单斜杠、横线、色块和叉号，而非只数绿色像素。</summary>
    private static bool IsCheckmark(List<Point> points, int lineHeight)
    {
        var box = Bounds(points);
        if (points.Count < 5 || box.Width < 3 || box.Height < 4 || box.Width > box.Height * 1.1
            || box.Height > lineHeight * 1.4 || box.Height < lineHeight * 0.45) return false;
        var columns = points.GroupBy(point => point.X).OrderBy(group => group.Key)
            .Select(group => group.Average(point => (double)point.Y) - box.Top).ToArray();
        if (columns.Length != box.Width) return false;
        var valley = Array.IndexOf(columns, columns.Max());
        if (valley <= 0 || valley >= columns.Length * 0.7) return false;
        // 小字号勾号左端有向下的衬线，端点中心不能代表短臂起点；用拐点前的局部最低中心。
        var leftArm = columns.Take(valley).Min();
        return leftArm >= box.Height * 0.4
            && columns[valley] - leftArm >= Math.Max(1, box.Height * 0.15)
            && columns[valley] - columns[^1] >= Math.Max(2, box.Height * 0.45)
            && columns[0] - columns[^1] >= box.Height * 0.1;
    }

    private static Rectangle Bounds(List<Point> points) => Rectangle.FromLTRB(points.Min(point => point.X), points.Min(point => point.Y),
        points.Max(point => point.X) + 1, points.Max(point => point.Y) + 1);

    private static string Describe(List<Point> points)
    {
        var box = Bounds(points);
        var columns = points.GroupBy(point => point.X).OrderBy(group => group.Key)
            .Select(group => group.Average(point => (double)point.Y) - box.Top).ToArray();
        return $"{box.Width}×{box.Height}/{points.Count}点/列中心[{string.Join(",", columns.Select(y => y.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))}]";
    }
}
