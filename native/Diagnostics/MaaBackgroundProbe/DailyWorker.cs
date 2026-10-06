using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BqtjLauncher.Application;
using BqtjLauncher.Domain;

namespace MaaBackgroundProbe;

/// <summary>单会话视觉执行器；只使用面板绑定的Flash句柄，不枚举桌面或读取登录凭据。</summary>
internal sealed class DailyWorker : IDisposable
{
    private sealed record Request(AutomationWindowTarget Target, int SaveNumber, Guid RunId,
        int ParentProcessId, string AutomationDirectory);
    private readonly Request _request;
    private readonly Process _parent;
    private readonly EventWaitHandle _stop;
    private readonly LiveTarget _target;
    private readonly Size _size;
    private IntPtr _controller;
    private MaaOcrRecognizer? _ocr;
    private long _capturedAt;

    private DailyWorker(Request request)
    {
        if (request.RunId == Guid.Empty || request.Target.SessionId == Guid.Empty
            || request.SaveNumber is < 1 or > 8 || request.ParentProcessId <= 0)
            throw new InvalidOperationException("运行身份不合法。");
        _request = request;
        _parent = Process.GetProcessById(request.ParentProcessId);
        _stop = EventWaitHandle.OpenExisting("Local\\bqtj-daily-stop-" + request.RunId.ToString("N"));
        _target = new LiveTarget(new IntPtr(request.Target.FlashWindowHandle), (uint)request.Target.NativeProcessId,
            "MacromediaFlashPlayerActiveX");
        _size = new Size(request.Target.Width, request.Target.Height);
        // 首版只支持已校准的原生950×600画面；其他比例停止而不是猜坐标。
        if (_size != new Size(950, 600)) throw new DailyScriptExecutionException("绑定游戏窗口",
            DailyScriptFailureCode.UnsupportedSize, _size.Width, _size.Height);
    }

    public static int Run()
    {
        DailyWorker? worker = null;
        try
        {
            var line = Console.ReadLine();
            if (line is null || line.Length > 4096) return 2;
            var request = JsonSerializer.Deserialize<Request>(line) ?? throw new InvalidOperationException();
            worker = new DailyWorker(request);
            worker.ExecuteAsync().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception exception)
        {
            // 不输出异常原文：截图、OCR文字及游戏信息只驻留内存。
            var failure = exception as DailyScriptExecutionException
                ?? new DailyScriptExecutionException(worker?._stage, worker?._failureCode ?? DailyScriptFailureCode.Initialization);
            Write(new { Kind = "Error", failure.Stage, failure.Code, failure.Width, failure.Height });
            return 1;
        }
        finally { worker?.Dispose(); }
    }

    private async Task ExecuteAsync()
    {
        Stage("初始化视觉组件");
        MaaNative.Initialize(Path.Combine(_request.AutomationDirectory, "maa-5.14.2-win-x64", "bin"));
        // 关闭库日志和截图落盘；本模式只向面板输出结构化进度。
        foreach (var option in new[] { 2, 6, 7 })
            if (MaaNative.MaaGlobalSetOption(option, [0], 1) == 0) throw new InvalidOperationException();
        if (MaaNative.MaaGlobalSetOption(4, BitConverter.GetBytes(0), 4) == 0) throw new InvalidOperationException();
        Stage("绑定游戏窗口");
        var visibleDeadline = Stopwatch.StartNew();
        while (!IsWindowVisible(_target.Handle) && visibleDeadline.Elapsed < TimeSpan.FromSeconds(20))
        { AssertIdentity(); await Task.Delay(250); }
        AssertValid();
        _controller = MaaNative.MaaWin32ControllerCreate(_target.Handle, 16, 4, 4);
        if (_controller == IntPtr.Zero || MaaNative.MaaControllerSetOption(_controller, 3, [1], 1) == 0)
            throw new InvalidOperationException();
        await WaitControllerAsync(MaaNative.MaaControllerPostConnection(_controller));
        Stage("加载文字识别");
        _failureCode = DailyScriptFailureCode.Ocr;
        _ocr = await MaaOcrRecognizer.CreateAsync(_request.AutomationDirectory, AssertValid);

        // 存档内容可能含用户名称，只识别标题，再验证两列四行卡片边框布局。
        await OpenSaveSelectionAsync();
        var slot = await WaitSaveLayoutAsync();
        Stage("进入指定存档");
        await ClickAsync(new Point(slot.X, slot.Y));
        var assistant = await WaitTextAsync(new Rectangle(0, 0, 950, 100), text => text == "助理");
        Stage("打开助理");
        await ClickAsync(new Point(assistant.Left + assistant.Width / 2, assistant.Top - 20));
        var script = await WaitTextAsync(new Rectangle(90, 70, 210, 340), text => text == "日常操作复制");
        Stage("选择日常操作复制");
        await ClickAsync(Center(script));
        // 代码标题提供选中证据，防止点击其他脚本残留的运行按钮。
        await WaitTextAsync(new Rectangle(300, 100, 350, 95), text => text.Trim('《', '》', '〈', '〉') == "日常操作复制");
        var run = await WaitTextAsync(new Rectangle(480, 475, 200, 55), text => text == "运行");
        await ClickAsync(Center(run));
        Stage("确认运行");
        var confirm = await WaitRunConfirmationAsync();
        await ClickAsync(Center(confirm));
        Stage("观察日常进度与最终保存");
        while (true)
        {
            using var frame = await CaptureAsync();
            var capturedAt = _capturedAt;
            var scene = await DailyProgressReader.ReadAsync(frame, _ocr, AssertValid);
            AssertValid();
            Write(new
            {
                Kind = "Frame",
                Frame = new DailyScriptFrame(_request.Target.SessionId, _request.RunId,
                capturedAt, scene.Progress, scene.FinalSaveChecked, scene.HasFailedItems)
            });
            await Task.Delay(1000);
        }
    }

    /// <summary>首页需先读取存档；弹窗已存在时不点击其背后的入口，加载慢也不重复点击。</summary>
    private async Task OpenSaveSelectionAsync()
    {
        Rectangle? title = null;
        Rectangle? readButton = null;
        Stage("读取存档");
        await DailySaveEntryFlow.EnsureAsync(async _ =>
        {
            using var frame = await CaptureAsync();
            title = await FindTextAsync(frame, new Rectangle(240, 135, 480, 65), DailyMenuGeometry.IsSaveDialogTitle);
            if (title is not null) return new DailySaveEntryObservation(true, false);
            readButton = await FindTextAsync(frame, new Rectangle(350, 530, 270, 70), text => text == "读取存档");
            Write(new { Kind = "Stage", Stage = _stage });
            return new DailySaveEntryObservation(false, readButton is not null);
        }, async _ =>
        {
            await ClickAsync(Center(readButton!.Value));
            Stage("等待存档选择");
        }, TimeSpan.FromSeconds(120), default);

    }

    /// <summary>同帧识别标题与卡片并等待连续稳定，避免弹窗动画期间复用上一帧锚点。</summary>
    private Task<DailySaveSlot> WaitSaveLayoutAsync()
    {
        Stage("等待存档选择");
        return DailySaveLayoutFlow.WaitAsync(async _ =>
        {
            using var frame = await CaptureAsync();
            var title = await FindTextAsync(frame, new Rectangle(240, 135, 480, 65), DailyMenuGeometry.IsSaveDialogTitle);
            Write(new { Kind = "Stage", Stage = _stage });
            if (title is null) return null;
            _failureCode = DailyScriptFailureCode.SaveLayout;
            try
            {
                var point = DailyMenuGeometry.FindSaveSlot(frame, title.Value, _request.SaveNumber);
                return new DailySaveSlot(point.X, point.Y);
            }
            catch (InvalidOperationException) { return null; }
        }, TimeSpan.FromSeconds(20), default);
    }
    /// <summary>同帧确认具名运行提示和唯一确定按钮；句尾标点可遗漏，输入只发送一次。</summary>
    private async Task<Rectangle> WaitRunConfirmationAsync()
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(120))
        {
            using var frame = await CaptureAsync();
            var prompt = await FindTextAsync(frame, new Rectangle(260, 170, 450, 100), DailyRunPrompt.IsExpected);
            if (prompt is not null)
            {
                var confirm = await FindTextAsync(frame, new Rectangle(345, 295, 100, 60), text => text == "确定");
                if (confirm is Rectangle button) return button;
            }
            Write(new { Kind = "Stage", Stage = _stage });
            await Task.Delay(500);
        }
        throw new DailyScriptExecutionException(_stage, DailyScriptFailureCode.MenuNotFound);
    }
    /// <summary>限定ROI、放大后识别唯一文字；每轮重新截图，点击不自动重试。</summary>
    private async Task<Rectangle> WaitTextAsync(Rectangle region, Func<string, bool> match)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(120))
        {
            using var frame = await CaptureAsync();
            var found = await FindTextAsync(frame, region, match);
            if (found is Rectangle box) return box;
            Write(new { Kind = "Stage", Stage = _stage });
            await Task.Delay(500);
        }
        throw new DailyScriptExecutionException(_stage, DailyScriptFailureCode.MenuNotFound);
    }

    /// <summary>同一帧内识别目标，坐标回映原图；拒绝歧义及过期截图，不猜点击位置。</summary>
    private async Task<Rectangle?> FindTextAsync(Bitmap frame, Rectangle region, Func<string, bool> match)
    {
        using var crop = frame.Clone(region, PixelFormat.Format24bppRgb);
        using var enlarged = new Bitmap(crop, new Size(crop.Width * 2, crop.Height * 2));
        _failureCode = DailyScriptFailureCode.Ocr;
        var lines = await _ocr!.ReadAsync(enlarged, AssertValid);
        var matches = lines.Where(item => match(Compact(item.Text))).ToArray();
        if (matches.Length > 1) throw new DailyScriptExecutionException(_stage, DailyScriptFailureCode.MenuAmbiguous);
        if (matches.Length == 0) return null;
        // OCR返回整数框，扩大到原图像素覆盖范围，再检查证据年龄。
        if (Stopwatch.GetElapsedTime(_capturedAt) > TimeSpan.FromSeconds(5)) throw new TimeoutException();
        var box = matches[0].Bounds;
        return new Rectangle(region.X + box.X / 2, region.Y + box.Y / 2,
            Math.Max(1, (box.Width + 1) / 2), Math.Max(1, (box.Height + 1) / 2));
    }

    private string _stage = "初始化视觉组件";
    private DailyScriptFailureCode _failureCode = DailyScriptFailureCode.Initialization;
    private void Stage(string value) { _stage = value; Write(new { Kind = "Stage", Stage = value }); }
    private static void Write(object value) { Console.WriteLine(JsonSerializer.Serialize(value)); Console.Out.Flush(); }
    private static string Compact(string text) => string.Concat(text.Normalize(NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c)));
    private static Point Center(Rectangle rectangle) => new(rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Height / 2);

    private Task ClickAsync(Point point)
    {
        _failureCode = DailyScriptFailureCode.Input;
        if (Stopwatch.GetElapsedTime(_capturedAt) > TimeSpan.FromSeconds(5)) throw new TimeoutException();
        // 序列开始/结束才检查停止信号，避免取消发生在按下后却阻止finally中的释放。
        return DirectChildInput.ClickWindowAsync(_target.Handle, point, () => { AssertIdentity(); return LiveTargets.RequireVisibleClient(_target); },
            AssertValid, AssertTargetIdentity);
    }

    /// <summary>截图及输入前核对面板、容器、原生窗口归属；隐藏、最小化、锁屏均停止。</summary>
    private void AssertIdentity()
    {
        if (_parent.HasExited) throw new DailyScriptExecutionException(_stage, DailyScriptFailureCode.TargetChanged);
        AssertTargetIdentity();
    }

    /// <summary>释放消息仍核对原绑定目标；不因停止、锁屏或最小化而遗留合成按下状态。</summary>
    private void AssertTargetIdentity()
    {
        if (!LiveTargets.IsValid(_target)
            || GetWindowThreadProcessId(GetAncestor(_target.Handle, 2), out var pid) == 0
            || pid != _request.Target.ContainerProcessId) throw new DailyScriptExecutionException(_stage, DailyScriptFailureCode.TargetChanged);
    }

    private void AssertValid()
    {
        AssertIdentity();
        if (_stop.WaitOne(0)) throw new OperationCanceledException();
        Size size;
        try { size = LiveTargets.RequireVisibleClient(_target); }
        catch (InvalidOperationException) { throw new DailyScriptExecutionException(_stage, DailyScriptFailureCode.TargetHidden); }
        if (size != _size) throw new DailyScriptExecutionException(_stage, DailyScriptFailureCode.Screenshot);
        var desktop = OpenInputDesktop(0, false, 1);
        if (desktop == IntPtr.Zero) throw new DailyScriptExecutionException(_stage, DailyScriptFailureCode.DesktopUnavailable);
        try
        {
            var name = new char[256];
            if (!GetUserObjectInformationW(desktop, 2, name, 512, out _) || new string(name).TrimEnd('\0') != "Default")
                throw new DailyScriptExecutionException(_stage, DailyScriptFailureCode.DesktopUnavailable);
        }
        finally { CloseDesktop(desktop); }
    }

    private async Task<Bitmap> CaptureAsync()
    {
        AssertValid();
        _failureCode = DailyScriptFailureCode.Screenshot;
        await WaitControllerAsync(MaaNative.MaaControllerPostScreencap(_controller));
        _capturedAt = Stopwatch.GetTimestamp();
        var image = MaaNative.MaaImageBufferCreate();
        try
        {
            if (image == IntPtr.Zero || MaaNative.MaaControllerCachedImage(_controller, image) == 0) throw new InvalidOperationException();
            var length = MaaNative.MaaImageBufferGetEncodedSize(image);
            var data = MaaNative.MaaImageBufferGetEncoded(image);
            if (length is 0 or > 32 * 1024 * 1024 || data == IntPtr.Zero) throw new InvalidOperationException();
            var bytes = new byte[(int)length];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            using var stream = new MemoryStream(bytes);
            using var decoded = Image.FromStream(stream);
            if (decoded.Size != _size) throw new InvalidOperationException();
            AssertValid();
            return new Bitmap(decoded);
        }
        finally { if (image != IntPtr.Zero) MaaNative.MaaImageBufferDestroy(image); }
    }

    private async Task WaitControllerAsync(long id)
    {
        var timer = Stopwatch.StartNew();
        while (id != 0 && timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            AssertValid();
            var status = MaaNative.MaaControllerStatus(_controller, id);
            if (status == 3000) return;
            if (status is not (1000 or 2000)) throw new InvalidOperationException();
            await Task.Delay(25);
        }
        throw new TimeoutException();
    }

    public void Dispose()
    {
        _ocr?.Dispose();
        if (_controller != IntPtr.Zero) MaaNative.MaaControllerDestroy(_controller);
        _parent.Dispose();
        _stop.Dispose();
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformationW(IntPtr handle, int index,
        [Out] char[] information, uint size, out uint needed);
}
