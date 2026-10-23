using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using BqtjLauncher.Application;
using BqtjLauncher.Runtime.Flash;

/// <summary>授权现场的只读像素校准；显式指定容器及原生PID，不点击、不访问生产账号库。</summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
    private static async Task<int> Main(string[] args)
    {
        try { await RunAsync(args); return 0; }
        catch (Exception error)
        {
            // 无交互桌面或目标已关闭是诊断失败，不应触发Windows应用程序崩溃弹窗。
            // 仅回报视觉层白名单错误码，不输出可能包含用户路径或OCR正文的异常消息。
            var code = error.Data["ScriptVisionCode"] as string;
            Console.Error.WriteLine($"只读诊断未完成（{error.GetType().Name}{(code is null ? string.Empty : "/" + code)}），请确认目标窗口、进程和桌面会话。");
            return 1;
        }
    }

    private static async Task RunAsync(string[] args)
    {
        if (args.Length == 6 && args[0] == "--scores")
        {
            // 只读比较指定区域的本体图案与锚点误差，区分资源样式不匹配和天气覆盖，不调整生产阈值。
            var image = LoadPixels(args[1]);
            var region = args.Skip(2).Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            var scoreMethod = typeof(SkyTopDropVision).GetMethod("Score", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            foreach (var pattern in SkyTopDropVision.Load("天空顶").Patterns.Where(item => item.Kind is SkyTopDropVision.DropKind.ColdGem or SkyTopDropVision.DropKind.HadesCodex or SkyTopDropVision.DropKind.GuardGem))
            {
                var best = (score: double.MinValue, x: 0, y: 0, error: 0);
                for (var y = region[1]; y < region[1] + region[3] && y < image.Height - pattern.Height; y++)
                    for (var x = region[0]; x < region[0] + region[2] && x < image.Width - pattern.Width; x++)
                    {
                        var score = (double)scoreMethod.Invoke(null, [image, x, y, pattern])!;
                        if (score <= best.score) continue;
                        var anchor = pattern.Anchor; var offset = ((y + anchor.Y) * image.Width + x + anchor.X) * 3;
                        best = (score, x, y, Math.Abs(image.Bgr[offset] - anchor.B) + Math.Abs(image.Bgr[offset + 1] - anchor.G) + Math.Abs(image.Bgr[offset + 2] - anchor.R));
                    }
                Console.WriteLine(JsonSerializer.Serialize(new { pattern.Name, pattern.Width, pattern.Height, best.score, best.x, best.y, anchorError = best.error }, JsonOptions));
            }
            return;
        }
        if (args.Length == 3 && args[0] == "--quota-offline")
        {
            var text = JsonSerializer.Deserialize<ScriptText[]>(File.ReadAllText(Path.GetFullPath(args[2])))!;
            Console.WriteLine(JsonSerializer.Serialize(SkyTopSceneVision.ReadQuota(LoadPixels(args[1]), text), JsonOptions));
            return;
        }
        if (args.Length >= 2 && args[0] == "--mini")
        {
            foreach (var path in args.Skip(1))
            {
                var pixels = LoadPixels(path); var region = new ScriptRect(810, 25, 116, 41);
                Console.WriteLine(JsonSerializer.Serialize(new { ai = SkyTopSceneVision.ReadBattleAi(pixels),
                    green = SkyTopMiniMap.Find(pixels, region, false), red = SkyTopMiniMap.Find(pixels, region, true) }, JsonOptions));
            }
            return;
        }
        if (args.Length == 3 && args[0] == "--motion")
        {
            var motion = new SkyTopSceneMotion();
            foreach (var path in args.Skip(1))
            {
                var pixels = LoadPixels(path);
                var player = SkyTopSceneVision.FindPlayer(pixels); var arrow = SkyTopSceneVision.FindExitArrow(pixels);
                var drops = SkyTopDropVision.Find(pixels, player, arrow);
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                var offset = motion.Observe(pixels, drops, player, arrow);
                Console.WriteLine(JsonSerializer.Serialize(new { player, offset, motion.HasEstimate, motion.Registration, milliseconds = elapsed.Elapsed.TotalMilliseconds }, JsonOptions));
            }
            return;
        }
        if (args.Length == 3 && args[0] == "--offline")
        {
            using var original = new System.Drawing.Bitmap(Path.GetFullPath(args[1]));
            using var bitmap = new System.Drawing.Bitmap(original.Width, original.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) graphics.DrawImageUnscaled(original, 0, 0);
            var data = bitmap.LockBits(new(0, 0, bitmap.Width, bitmap.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            var bytes = new byte[bitmap.Width * bitmap.Height * 3];
            try { for (var row = 0; row < bitmap.Height; row++) Marshal.Copy(data.Scan0 + row * data.Stride, bytes, row * bitmap.Width * 3, bitmap.Width * 3); }
            finally { bitmap.UnlockBits(data); }
            var pixels = new ScriptPixels(bitmap.Width, bitmap.Height, bytes);
            var text = JsonSerializer.Deserialize<ScriptText[]>(File.ReadAllText(Path.GetFullPath(args[2])))!;
            var arrow = SkyTopSceneVision.FindExitArrow(pixels, out var shapes);
            var player = SkyTopSceneVision.FindPlayer(pixels);
            var scan = System.Diagnostics.Stopwatch.StartNew();
            var drops = SkyTopDropVision.Find(pixels, player, arrow);
            var scanMilliseconds = scan.Elapsed.TotalMilliseconds;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                round = SkyTopSceneVision.ReadRound(text),
                player,
                arrow,
                shapes,
                scanMilliseconds,
                drops
            }, JsonOptions));
            // 分类图仅供本地核验；不发送走位，也不把整幅用户画面加入源码。
            using var annotated = new System.Drawing.Bitmap(bitmap.Width + 330, Math.Max(bitmap.Height, 90 + drops.Count * 52));
            using (var graphics = System.Drawing.Graphics.FromImage(annotated))
            using (var font = new System.Drawing.Font("Microsoft YaHei", 11))
            {
                graphics.Clear(System.Drawing.Color.FromArgb(15, 23, 35));
                graphics.DrawImageUnscaled(bitmap, 0, 0);
                graphics.DrawString("物品分类（只观察，不拾取）", font, System.Drawing.Brushes.White, 960, 15);
                for (var index = 0; index < drops.Count; index++)
                {
                    var drop = drops[index];
                    var color = drop.Required ? System.Drawing.Color.Lime : System.Drawing.Color.Orange;
                    using var pen = new System.Drawing.Pen(color, 2);
                    using var brush = new System.Drawing.SolidBrush(color);
                    graphics.DrawRectangle(pen, drop.Bounds.X, drop.Bounds.Y, drop.Bounds.Width, drop.Bounds.Height);
                    graphics.DrawString((index + 1).ToString(CultureInfo.InvariantCulture), font, brush, drop.Bounds.X, Math.Max(0, drop.Bounds.Y - 20));
                    graphics.DrawString($"{index + 1}. {drop.Name}{(drop.Overlapping ? "（重叠区域）" : "")}\n    {(drop.Required ? "优先材料" : "顺带 / 不绕路")}", font, brush, 960, 55 + index * 52);
                }
            }
            annotated.Save(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "classified.png"), System.Drawing.Imaging.ImageFormat.Png);
            return;
        }
        if (args.Length is < 4 or > 6) throw new ArgumentException("containerPid nativePid output componentDirectory [templates] [features]");
        var container = int.Parse(args[0], CultureInfo.InvariantCulture); var native = int.Parse(args[1], CultureInfo.InvariantCulture);
        var output = Path.GetFullPath(args[2]); Directory.CreateDirectory(output);
        var watching = args.Length == 6 && args[4] == "--watch";
        var daily = args.Length == 5 && args[4] == "--daily";
        var system = args.Length == 5 && args[4] == "--system";
        var mode = args.Length == 5 && args[4] == "--mode";
        var quota = args.Length == 5 && args[4] == "--quota";
        var saveTitle = args.Length == 5 && args[4] == "--save-title";
        var frames = watching ? int.Parse(args[5], CultureInfo.InvariantCulture) : daily ? 4 : 1;
        if (frames is < 1 or > 60) throw new ArgumentException("只读连续采样帧数为1～60。");
        var found = new List<nint>();
        EnumWindows((root, argument) =>
        {
            _ = GetWindowThreadProcessId(root, out var owner);
            if (owner != container) return true;
            EnumChildWindows(root, (child, childArgument) =>
            {
                _ = GetWindowThreadProcessId(child, out var pid);
                var name = new char[128]; var length = GetClassNameW(child, name, name.Length);
                if (pid == native && new string(name, 0, length) == "MacromediaFlashPlayerActiveX") found.Add(child);
                return true;
            }, 0);
            return true;
        }, 0);
        if (found.Count != 1)
        {
            Console.Error.WriteLine($"指定Flash窗口数：{found.Count}");
            throw new InvalidOperationException("没有唯一的指定Flash窗口。");
        }
        var target = new AutomationWindowTarget(Guid.NewGuid(), Guid.NewGuid(), container, native, found[0].ToInt64(), 950, 600);
        await using var vision = new SharedScriptVision(Path.GetFullPath(args[3]));
        await vision.BeginAsync(default);
        try
        {
            // 连续采样只观察几何和截图，便于分辨人物移动、相机位移与箭头动画，不向游戏输入。
            for (var sample = 0; sample < frames; sample++)
            {
                var frameOutput = watching || daily ? Path.Combine(output, sample.ToString("D3", CultureInfo.InvariantCulture)) : output;
                Directory.CreateDirectory(frameOutput);
                if (saveTitle)
                {
                    // 分开验证截图与选档标题OCR；首页没有标题时也应正常返回空结果。
                    var region = new ScriptRect(240, 135, 480, 65);
                    var captured = await vision.CaptureAsync(target, default);
                    using var titleBitmap = OfficialScriptExecutor.ToBitmap(captured.Pixels);
                    using var title = titleBitmap.Clone(new System.Drawing.Rectangle(region.X, region.Y, region.Width, region.Height), titleBitmap.PixelFormat);
                    title.Save(Path.Combine(frameOutput, "save-title.png"), System.Drawing.Imaging.ImageFormat.Png);
                    Console.WriteLine("save-title-capture-ok");
                    try
                    {
                        var result = await vision.ReadAsync(target, region, default, captured.Frame.SnapshotId);
                        File.WriteAllText(Path.Combine(frameOutput, "save-title-ocr.json"), JsonSerializer.Serialize(result.Text, JsonOptions));
                        Console.WriteLine($"save-title-ocr-ok: {result.Text.Count}");
                    }
                    catch (InvalidOperationException error)
                    {
                        Console.WriteLine($"save-title-ocr-error: {error.Data["ScriptVisionCode"]}");
                        throw;
                    }
                    continue;
                }
                if (quota)
                {
                    // 与执行器使用相同ROI和同帧像素，只保留弹层里的材料及计数，不输出整页OCR。
                    var quotaFrame = await vision.ReadAsync(target, new(0, 0, 600, 600), default);
                    var quotaPixels = await vision.PixelsAsync(target, new(0, 0, 950, 600), quotaFrame.SnapshotId, default);
                    // 与整页同一截图的小计数行对照，区分弹层未显示与蓝色数字被整页OCR漏读。
                    var labels = quotaFrame.Text.Where(line => line.Text.Contains("冥王图鉴", StringComparison.Ordinal)).ToArray();
                    IReadOnlyList<ScriptText> counters = [];
                    // 无弹层本身就是有效对照，必须保存空样本，不能因Single抛错而误报为视觉启动失败。
                    if (labels.Length == 1)
                    {
                        var label = labels[0].Bounds;
                        var counterRegion = new ScriptRect(Math.Max(0, label.X + label.Width - 8), Math.Max(0, label.Y - 3), 110,
                            Math.Min(label.Height + 6, 600 - Math.Max(0, label.Y - 3)));
                        var counterFrame = await vision.ReadAsync(target, counterRegion, default, quotaFrame.SnapshotId, ScriptTextStyle.QuotaCounter);
                        counters = counterFrame.Text.Where(line => System.Text.RegularExpressions.Regex.IsMatch(line.Text, @"\d")).ToArray();
                    }
                    Console.WriteLine(JsonSerializer.Serialize(new { counters, labelCount = labels.Length }, JsonOptions));
                    var lines = quotaFrame.Text.Where(line => line.Bounds.Y >= 495 && line.Bounds.X < 450
                        && (line.Text.Contains("宝石", StringComparison.Ordinal) || line.Text.Contains("图鉴", StringComparison.Ordinal)
                            || System.Text.RegularExpressions.Regex.IsMatch(line.Text, @"\d+\s*/\s*\d+"))).ToArray();
                    using var quotaBitmap = OfficialScriptExecutor.ToBitmap(quotaPixels);
                    quotaBitmap.Save(Path.Combine(frameOutput, "quota.png"), System.Drawing.Imaging.ImageFormat.Png);
                    File.WriteAllText(Path.Combine(frameOutput, "quota-ocr.json"), JsonSerializer.Serialize(lines, JsonOptions));
                    Console.WriteLine(JsonSerializer.Serialize(new { result = SkyTopSceneVision.ReadQuota(quotaPixels, lines.Concat(counters).ToArray()), lines }, JsonOptions));
                    continue;
                }
                if (system || mode)
                {
                    // 精确复用执行器的菜单ROI和OCR倍率，避免全图识别掩盖局部识别差异。
                    var region = mode ? new ScriptRect(260, 140, 440, 140) : new ScriptRect(90, 20, 760, 510);
                    var systemFrame = await vision.ReadAsync(target, region, default);
                    using var menu = OfficialScriptExecutor.ToBitmap(await vision.PixelsAsync(target, region, systemFrame.SnapshotId, default));
                    var name = mode ? "mode" : "system";
                    menu.Save(Path.Combine(frameOutput, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                    File.WriteAllText(Path.Combine(frameOutput, name + "-ocr.json"), JsonSerializer.Serialize(systemFrame.Text, JsonOptions));
                    Console.WriteLine(JsonSerializer.Serialize(systemFrame.Text, JsonOptions));
                    continue;
                }
                if (daily)
                {
                    // 与实际日常执行器使用完全相同的ROI及同帧像素，只输出标题和形状诊断。
                    var region = new ScriptRect(759, 154, 182, 420);
                    var sidebarFrame = await vision.ReadAsync(target, region, default);
                    using var sidebar = OfficialScriptExecutor.ToBitmap(await vision.PixelsAsync(target, region, sidebarFrame.SnapshotId, default));
                    var lines = sidebarFrame.Text.Select(line => new OcrLine(line.Text,
                        new(line.Bounds.X - region.X, line.Bounds.Y - region.Y, line.Bounds.Width, line.Bounds.Height), line.Confidence)).ToArray();
                    sidebar.Save(Path.Combine(frameOutput, "sidebar.png"), System.Drawing.Imaging.ImageFormat.Png);
                    File.WriteAllText(Path.Combine(frameOutput, "sidebar-ocr.json"), JsonSerializer.Serialize(new
                    { filtered = lines.Select(line => new { text = line.Text, box = new[] { line.Bounds.X, line.Bounds.Y, line.Bounds.Width, line.Bounds.Height }, score = line.Confidence }) }, JsonOptions));
                    var completed = GreenScriptVision.FindCompletedHeader(sidebar, lines, out var diagnostic);
                    Console.WriteLine(JsonSerializer.Serialize(new { sample, header = lines.Where(line => line.Text.Contains("日常操作复制", StringComparison.Ordinal)), completed = completed is not null, diagnostic }, JsonOptions));
                    if (sample + 1 < frames) await Task.Delay(1000);
                    continue;
                }
                var frame = await vision.ReadAsync(target, new(0, 0, 950, 600), default);
                var pixels = await vision.PixelsAsync(target, new(0, 0, 950, 600), frame.SnapshotId, default);
                using var bitmap = OfficialScriptExecutor.ToBitmap(pixels);
                bitmap.Save(Path.Combine(frameOutput, "scene.png"), System.Drawing.Imaging.ImageFormat.Png);
                File.WriteAllText(Path.Combine(frameOutput, "ocr.json"), JsonSerializer.Serialize(frame.Text, JsonOptions));
                var player = SkyTopSceneVision.FindPlayer(pixels); var arrow = SkyTopSceneVision.FindExitArrow(pixels);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    sample,
                    round = SkyTopSceneVision.ReadRound(frame.Text),
                    player,
                    arrow,
                    groups = SkyTopSceneVision.FindLootGroups(pixels, player, arrow)
                }, JsonOptions));
                if (args.Length >= 5 && !watching)
                    foreach (var file in Directory.EnumerateFiles(Path.GetFullPath(args[4]), "*.png"))
                    {
                        var matches = await vision.FindImagesAsync(target, new(0, 75, 940, 420), File.ReadAllBytes(file), frame.SnapshotId, default,
                            args.Length == 6 && args[5] == "features" ? ScriptImageAlgorithm.Features : ScriptImageAlgorithm.Template);
                        Console.WriteLine(JsonSerializer.Serialize(new { template = Path.GetFileName(file), matches }, JsonOptions));
                    }
                if (sample + 1 < frames) await Task.Delay(200);
            }
        }
        finally { await vision.ReleaseTargetAsync(target); await vision.EndAsync(); }
    }

    /// <summary>只读取指定本地图片，供镜头配准的离线回放，不绑定或操作窗口。</summary>
    private static ScriptPixels LoadPixels(string path)
    {
        using var image = new System.Drawing.Bitmap(path);
        var bytes = new byte[image.Width * image.Height * 3];
        for (var y = 0; y < image.Height; y++) for (var x = 0; x < image.Width; x++)
        {
            var color = image.GetPixel(x, y); var at = (y * image.Width + x) * 3;
            bytes[at] = color.B; bytes[at + 1] = color.G; bytes[at + 2] = color.R;
        }
        return new(image.Width, image.Height, bytes);
    }

    private delegate bool WindowCallback(nint window, nint argument);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint argument);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, WindowCallback callback, nint argument);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, [Out] char[] name, int capacity);
}
