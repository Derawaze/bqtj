using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MaaBackgroundProbe;

/// <summary>合成测试与人工Flash验证入口；默认打开验证窗口，不自动控制游戏。</summary>
internal static class Program
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };
    [STAThread]
    private static int Main(string[] args)
    {
        // 日常分支也须先设置与原生宿主一致的DPI模式，避免窗口校验和截图坐标被虚拟化。
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        if (args is ["--daily-worker"]) return DailyWorker.Run();
        if (args.Length == 0)
        {
            var root = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
            args = [Path.Combine(root, "maa-5.14.2-win-x64", "bin"), Path.Combine(root, "live-" + Guid.NewGuid().ToString("N")), "Inspect"];
        }
        if (args.Length != 3 || args[2] is not ("PrintWindow" or "FramePool" or "Inspect" or "DirectChild" or "Template" or "OCR"))
        {
            Console.Error.WriteLine("Usage: MaaBackgroundProbe <verified-bin-directory> <output-directory> PrintWindow|FramePool|DirectChild|Template|OCR|Inspect");
            return 2;
        }

        Application.EnableVisualStyles();
        var output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        if (args[2] == "DirectChild") return DirectChildRegression.Run(output);
        MaaNative.Initialize(Path.GetFullPath(args[0]));
        SetGlobal(1, Encoding.UTF8.GetBytes(Path.Combine(output, args[2] == "Inspect" ? "runtime-logs" : "synthetic-logs")));
        SetGlobal(4, BitConverter.GetBytes(0));
        SetGlobal(2, [0]);
        SetGlobal(6, [0]);
        SetGlobal(7, [0]);
        if (args[2] == "Template") return TemplateRegression.Run(output);
        if (args[2] == "OCR") return OcrRegression.Run(output, Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(args[0])))!);

        if (args[2] == "Inspect")
        {
            Application.Run(new LiveProbeWindow());
            return 0;
        }

        using var first = new ProbeWindow("Synthetic A", Color.Red, Color.Yellow);
        using var second = new ProbeWindow("Synthetic B", Color.Blue, Color.Lime);
        using var cover = new ProbeWindow("Bqtj automation test — synthetic windows only", Color.Gray, Color.Gray);
        var location = Screen.PrimaryScreen!.WorkingArea.Location + new Size(40, 40);
        first.Location = second.Location = cover.Location = location;
        var exitCode = 1;
        cover.Shown += async (_, _) =>
        {
            var controllers = new List<IntPtr>();
            string? error = null;
            try
            {
                await Task.Delay(200);
                var foreground = GetForegroundWindow();
                GetCursorPos(out var cursor);
                void AssertUndisturbed()
                {
                    GetCursorPos(out var current);
                    if (GetForegroundWindow() != foreground || current != cursor)
                        throw new InvalidOperationException("前台焦点或鼠标位置发生变化；本次不通过（也可能有人为干预），请重新验证。");
                }
                var method = args[2] == "PrintWindow" ? 16UL : 2UL;
                foreach (var form in new[] { first, second })
                {
                    var controller = MaaNative.MaaWin32ControllerCreate(form.Handle, method, 4, 4);
                    if (controller == IntPtr.Zero) throw new InvalidOperationException("无法创建合成窗口控制器。");
                    controllers.Add(controller);
                    if (MaaNative.MaaControllerSetOption(controller, 3, [1], 1) == 0)
                        throw new InvalidOperationException("无法启用原始尺寸截图。");
                    await WaitAsync(controller, MaaNative.MaaControllerPostConnection(controller), AssertUndisturbed);
                }

                // 遮挡窗口始终位于测试目标上方；返回灰色意味着截到了遮挡物，而不是目标窗口。
                AssertCovered(first, cover);
                await AssertColorAsync(controllers[0], first.ClientSize, Color.Red, AssertUndisturbed);
                await AssertColorAsync(controllers[1], second.ClientSize, Color.Blue, AssertUndisturbed);
                await WaitAsync(controllers[0], MaaNative.MaaControllerPostClick(controllers[0], 40, 40), AssertUndisturbed);
                await Task.Delay(100);
                if (first.Clicks != 1 || second.Clicks != 0 || cover.Clicks != 0)
                    throw new InvalidOperationException("A点击未送达或串到了其他窗口。");
                await AssertColorAsync(controllers[0], first.ClientSize, Color.Yellow, AssertUndisturbed);
                await AssertColorAsync(controllers[1], second.ClientSize, Color.Blue, AssertUndisturbed);
                await WaitAsync(controllers[1], MaaNative.MaaControllerPostClick(controllers[1], 40, 40), AssertUndisturbed);
                await Task.Delay(100);
                if (first.Clicks != 1 || second.Clicks != 1 || cover.Clicks != 0)
                    throw new InvalidOperationException("B点击未送达或串到了其他窗口。");
                await AssertColorAsync(controllers[1], second.ClientSize, Color.Lime, AssertUndisturbed);
                AssertCovered(first, cover);
                AssertUndisturbed();
                exitCode = 0;
            }
            catch (Exception ex) { error = ex.Message; }
            finally
            {
                // 外部运行器有硬超时；若第三方销毁接口阻塞，只终止本探针，不碰游戏进程。
                foreach (var controller in controllers) MaaNative.MaaControllerDestroy(controller);
                var report = new
                {
                    passed = exitCode == 0,
                    screenshot = args[2],
                    input = "PostMessage",
                    firstClicks = first.Clicks,
                    secondClicks = second.Clicks,
                    coverClicks = cover.Clicks,
                    clientWidth = first.ClientSize.Width,
                    clientHeight = first.ClientSize.Height,
                    error,
                    scope = "Synthetic windows only; Flash compatibility is NOT verified.",
                };
                var json = JsonSerializer.Serialize(report, ReportOptions);
                File.WriteAllText(Path.Combine(output, "result.json"), json);
                Console.WriteLine(json);
                cover.Close();
            }
        };
        first.Show();
        second.Show();
        cover.TopMost = true;
        Application.Run(cover);
        return exitCode;
    }

    /// <summary>等待异步C接口完成，有限轮询且保持UI消息泵工作。</summary>
    private static async Task WaitAsync(IntPtr controller, long id, Action assertUndisturbed)
    {
        if (id == 0) throw new InvalidOperationException("Maa拒绝操作。");
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            assertUndisturbed();
            var status = MaaNative.MaaControllerStatus(controller, id);
            if (status == 3000) return;
            if (status is 0 or 4000) throw new InvalidOperationException($"Maa操作失败，状态{status}。");
            await Task.Delay(20);
        }
        throw new TimeoutException("Maa操作超过8秒，不能认定后台能力通过。");
    }

    /// <summary>对比合成色块，既检测目标归属，也检测点击后截图是否更新。</summary>
    private static async Task AssertColorAsync(IntPtr controller, Size clientSize, Color expected, Action assertUndisturbed)
    {
        await WaitAsync(controller, MaaNative.MaaControllerPostScreencap(controller), assertUndisturbed);
        var image = MaaNative.MaaImageBufferCreate();
        if (image == IntPtr.Zero) throw new InvalidOperationException("无法分配图像缓冲。");
        try
        {
            if (MaaNative.MaaControllerCachedImage(controller, image) == 0)
                throw new InvalidOperationException("截图缓存为空。");
            var width = MaaNative.MaaImageBufferWidth(image);
            var height = MaaNative.MaaImageBufferHeight(image);
            // Windows可能按最小窗口宽度调整Form，按已创建客户区尺寸验证，而不是请求尺寸。
            if (width != clientSize.Width || height != clientSize.Height || MaaNative.MaaImageBufferType(image) != 16)
                throw new InvalidOperationException($"截图尺寸或格式不符：{width}x{height}，要求{clientSize.Width}x{clientSize.Height} BGR8。");
            var data = MaaNative.MaaImageBufferGetRawData(image);
            if (data == IntPtr.Zero) throw new InvalidOperationException("截图数据为空。");
            var offset = ((height / 2 * width) + width / 2) * 3;
            var actual = Color.FromArgb(Marshal.ReadByte(data, offset + 2), Marshal.ReadByte(data, offset + 1), Marshal.ReadByte(data, offset));
            if (actual.ToArgb() != expected.ToArgb())
                throw new InvalidOperationException($"目标截图色块不符：预期{expected}，实际{actual}。");
        }
        finally { MaaNative.MaaImageBufferDestroy(image); }
    }

    private static void SetGlobal(int key, byte[] value)
    {
        if (MaaNative.MaaGlobalSetOption(key, value, (ulong)value.Length) == 0)
            throw new InvalidOperationException($"无法设置Maa选项{key}。");
    }

    private static void AssertCovered(Form target, Form cover)
    {
        if (WindowFromPoint(target.PointToScreen(new Point(100, 80))) != cover.Handle)
            throw new InvalidOperationException("目标未被测试遮挡窗口覆盖，测试条件不成立。");
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);
}

/// <summary>不激活、无账号数据的测试窗口；只接受客户区指定位置的鼠标点击。</summary>
internal sealed class ProbeWindow : Form
{
    private readonly Color _clickedColor;
    public int Clicks { get; private set; }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE：测试不占用用户当前焦点。
            return parameters;
        }
    }

    public ProbeWindow(string title, Color initialColor, Color clickedColor)
    {
        Text = title;
        _clickedColor = clickedColor;
        BackColor = initialColor;
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(200, 160);
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || e.X != 40 || e.Y != 40) return;
        Clicks++;
        BackColor = _clickedColor;
        Refresh();
    }
}
