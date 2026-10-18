using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BqtjLauncher.Application;
using BqtjLauncher.Runtime.Flash;

/// <summary>只读诊断已授权的游戏选档区域；只枚举指定容器的Flash子窗口，无点击、切档或账号库访问。</summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static async Task Main(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("containerPid nativePid output componentDirectory");
        var containerPid = int.Parse(args[0], CultureInfo.InvariantCulture); var nativePid = int.Parse(args[1], CultureInfo.InvariantCulture);
        var output = Path.GetFullPath(args[2]); Directory.CreateDirectory(output);
        var found = new List<long>();
        EnumWindows((root, parameter) =>
        {
            _ = GetWindowThreadProcessId(root, out var owner);
            if (owner == containerPid)
                EnumChildWindows(root, (window, childParameter) =>
                {
                    _ = GetWindowThreadProcessId(window, out var process);
                    var name = new char[128]; var count = GetClassName(window, name, name.Length);
                    if (process == nativePid && new string(name, 0, count) == "MacromediaFlashPlayerActiveX") found.Add(window.ToInt64());
                    return true;
                }, 0);
            return true;
        }, 0);
        if (found.Count != 1) throw new InvalidOperationException("没有唯一的指定Flash目标。");
        var target = new AutomationWindowTarget(Guid.NewGuid(), Guid.NewGuid(), containerPid, nativePid, found[0], 950, 600);
        await using var vision = new SharedScriptVision(Path.GetFullPath(args[3]));
        await vision.BeginAsync(default);
        try
        {
            var titleFrame = await vision.ReadAsync(target, new(240, 135, 480, 65), default);
            var title = titleFrame.Text.Single(line => line.Text == "读取存档");
            using var image = OfficialScriptExecutor.ToBitmap(await vision.PixelsAsync(target, new(0, 0, 950, 600), titleFrame.SnapshotId, default));
            // 仅落盘弹窗，避免把选档范围之外的游戏及平台信息带入诊断证据。
            using var menu = image.Clone(new Rectangle(250, 130, 470, 330), image.PixelFormat);
            menu.Save(Path.Combine(output, "save-menu.png"), System.Drawing.Imaging.ImageFormat.Png);
            // 局部放大仅用于核对像素字体，不输出选档区域以外的截图。
            using (var enlarged = new Bitmap(menu.Width * 3, menu.Height * 3))
            {
                using var graphics = Graphics.FromImage(enlarged);
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                graphics.DrawImage(menu, new Rectangle(Point.Empty, enlarged.Size));
                enlarged.Save(Path.Combine(output, "save-menu-large.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            var results = new List<object>();
            for (var number = 1; number <= 8; number++)
            {
                var point = DailyMenuGeometry.FindSaveSlot(image,
                    new(title.Bounds.X, title.Bounds.Y, title.Bounds.Width, title.Bounds.Height), number);
                var roi = new ScriptRect(point.X - 91, point.Y - 20, 182, 40);
                var names = await vision.ReadAsync(target, roi, default, titleFrame.SnapshotId);
                var oldName = names.Text.Select(line => line.Text.Trim()).FirstOrDefault(value =>
                    !string.IsNullOrEmpty(value) && !value.StartsWith("存档", StringComparison.Ordinal) && !char.IsDigit(value[0])) ?? "未识别名称";
                var nameRegion = SaveSlotNameReader.NameRegion(point);
                var corrected = await vision.ReadAsync(target, nameRegion, default, titleFrame.SnapshotId, ScriptTextStyle.SaveName);
                results.Add(new { number, oldName, name = SaveSlotNameReader.Parse(corrected.Text), roi = nameRegion, text = corrected.Text });
            }
            var json = JsonSerializer.Serialize(results, JsonOptions);
            File.WriteAllText(Path.Combine(output, "names.json"), json);
            Console.OutputEncoding = Encoding.UTF8; Console.WriteLine(json);
        }
        finally { await vision.ReleaseTargetAsync(target); await vision.EndAsync(); }
    }

    private delegate bool EnumCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint root, EnumCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, [Out] char[] buffer, int capacity);
}
