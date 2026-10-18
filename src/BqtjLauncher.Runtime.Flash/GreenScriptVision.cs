using BqtjLauncher.Domain;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>同一帧官方脚本标题的完成证据；不表示服务端保存已持久化。</summary>
internal sealed record OfficialCompletionVisualEvidence(Rectangle Header, Rectangle Checkmark);

/// <summary>检查完整N/N标题左侧的绿色独立勾形；不依赖列表末行是否仍在可见区域。</summary>
internal static class GreenScriptVision
{
    // 保留抗锯齿的绿色笔画，排除白字、黄色数值和暗绿色背景。
    private static bool IsGreen(Color pixel) => pixel.G >= 70 && pixel.G - pixel.R >= 40 && pixel.G - pixel.B >= 35;

    /// <summary>标题OCR和像素必须属于同一帧，坐标以输入侧栏图片为原点；OCR勾字符本身不是证据。</summary>
    public static OfficialCompletionVisualEvidence? FindCompletedHeader(Bitmap frame, IReadOnlyList<OcrLine> lines)
        => FindCompletedHeader(frame, lines, out _);

    /// <summary>返回限定区域的形状统计，供只读现场回放使用；不读取账号或运行日志。</summary>
    public static OfficialCompletionVisualEvidence? FindCompletedHeader(Bitmap frame, IReadOnlyList<OcrLine> lines, out string diagnostic)
    {
        var candidates = lines.Where(line => DailyScriptProgressParser.Parse([line.Text]) is { } progress
            && progress.Completed == progress.Total).ToArray();
        diagnostic = $"全完成标题候选{candidates.Length}个";
        if (candidates.Length != 1) return null;
        var box = candidates[0].Bounds;
        if (box.Height < 4 || !new Rectangle(Point.Empty, frame.Size).Contains(box)) return null;
        // OCR框可能包含勾号，也可能从正文开始；向左留两个字高、向框内留一个字高。
        // 只检查标题这一行，其他日常步骤的绿勾不能替代整体完成标记。
        var search = Rectangle.Intersect(Rectangle.FromLTRB(box.Left - box.Height * 2, box.Top - 2,
            box.Left + box.Height, box.Bottom + 2), new Rectangle(Point.Empty, frame.Size));
        var components = Components(frame, search).ToArray();
        diagnostic += $"；标题框{box}；左侧笔画：" + string.Join("；", components.Select(Describe));
        var marks = components.Where(points => IsCheckmark(points, box.Height)).ToArray();
        return marks.Length == 1 ? new(box, Bounds(marks[0])) : null;
    }
    /// <summary>使用八邻接保留细小斜线笔画；分析范围限定在当前官方脚本标题的左侧。</summary>
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
