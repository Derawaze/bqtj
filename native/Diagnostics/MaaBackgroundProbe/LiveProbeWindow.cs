using System.Diagnostics;
using System.Runtime.InteropServices;
using BqtjLauncher.Application;
using BqtjLauncher.Domain;

namespace MaaBackgroundProbe;

/// <summary>人工参与的Flash后台兼容性探针；预览只驻留内存，每次点击都需新截图和显式选择。</summary>
internal sealed class LiveProbeWindow : Form
{
    private readonly ComboBox _targets = new() { Width = 410, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _method = new() { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _input = new() { Width = 135, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _ready = new() { Text = "目标已进入游戏，非登录页", AutoSize = true };
    private readonly Button _refresh = new() { Text = "刷新目标", AutoSize = true };
    private readonly Button _capture = new() { Text = "后台截图", AutoSize = true };
    private readonly Button _click = new() { Text = "后台单击一次", AutoSize = true, Enabled = false };
    private readonly Button _learn = new() { Text = "记住按钮图像", AutoSize = true, Enabled = false };
    private readonly Button _recognize = new() { Text = "识别并后台单击", AutoSize = true, Enabled = false };
    private readonly Button _region = new() { Text = "设为文字区域起点", AutoSize = true, Enabled = false };
    private readonly Button _ocr = new() { Text = "只读识别文字区域", AutoSize = true, Enabled = false };
    private readonly Button _progressCheck = new() { Text = "只读检查日常进度", AutoSize = true };
    private readonly Button _runConfirm = new() { Text = "只读检查运行确认", AutoSize = true };
    private readonly Button _saveMenu = new() { Text = "只读检查存档菜单", AutoSize = true };
    private readonly Button _observe = new() { Text = "连续只读观测", AutoSize = true, Enabled = false };
    private readonly PictureBox _preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 64, Text = "先选目标并确认已进入游戏。截图不会保存到磁盘。" };
    // 仅保留有限的结构化状态变化，不保存原始OCR文字、截图或用户数据到磁盘。
    private readonly TextBox _trace = new() { Dock = DockStyle.Bottom, Height = 100, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private IntPtr _controller;
    private LiveTarget? _bound;
    private Point? _point;
    private long _capturedAt;
    private bool _busy;
    private Bitmap? _template;
    private Size _templateFrameSize;
    private Point? _regionStart;
    private Rectangle? _ocrRegion;
    private bool _observing;
    private CancellationTokenSource? _observationCancellation;

    public LiveProbeWindow()
    {
        Text = "Flash后台验证（开发探针）";
        ClientSize = new Size(1000, 760);
        MinimumSize = new Size(820, 600);
        // 控件随DPI和窗口宽度换行，顶栏按内容增长，避免第三行OCR按钮被裁切。
        var controls = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8) };
        controls.Controls.AddRange([_targets, _method, _input, _refresh, _ready, _capture, _click, _learn, _recognize, _region, _ocr, _observe, _saveMenu, _runConfirm, _progressCheck]);
        Controls.Add(_preview);
        Controls.Add(_status);
        Controls.Add(_trace);
        Controls.Add(controls);
        _method.Items.AddRange(["PrintWindow", "FramePool"]);
        _method.SelectedIndex = 0;
        _input.Items.AddRange(["PostMessage", "SendMessage", "DirectChild"]);
        _input.SelectedIndex = 2; // 实机已通过的定向子窗口模式；其他模式保留作对照。
        _refresh.Click += (_, _) => RefreshTargets();
        _targets.SelectedIndexChanged += (_, _) => { ResetBinding(); _ready.Checked = false; };
        _method.SelectedIndexChanged += (_, _) => ResetBinding();
        _input.SelectedIndexChanged += (_, _) => ResetBinding();
        _ready.CheckedChanged += (_, _) => ClearPreview();
        _capture.Click += async (_, _) => await RunOperationAsync(CaptureAsync);
        _click.Click += async (_, _) => await RunOperationAsync(ClickAsync);
        _learn.Click += (_, _) => LearnTemplate();
        _recognize.Click += async (_, _) => await RunOperationAsync(RecognizeClickAsync);
        _region.Click += (_, _) =>
        {
            _regionStart = _point;
            _ocrRegion = null;
            _point = null;
            _click.Enabled = _learn.Enabled = _region.Enabled = _ocr.Enabled = false;
            _status.Text = "请在同一预览中选择文字区域另一角，仅选择官方脚本侧栏，勿选账号信息。不会发送游戏输入。";
        };
        _progressCheck.Click += async (_, _) => await RunOperationAsync(ReadDailyProgressAsync);
        _runConfirm.Click += async (_, _) => await RunOperationAsync(ReadRunConfirmationAsync);
        _saveMenu.Click += async (_, _) => await RunOperationAsync(ReadSaveMenuAsync);
        _ocr.Click += async (_, _) => await RunOperationAsync(ReadRegionAsync);
        _observe.Click += async (_, _) =>
        {
            if (_observing) _observationCancellation?.Cancel();
            else await ObserveLoopAsync();
        };
        _preview.MouseClick += (_, e) => SelectPoint(e.Location);
        Shown += (_, _) => RefreshTargets();
        FormClosing += (_, e) =>
        {
            if (_observing)
            {
                _observationCancellation?.Cancel();
                e.Cancel = true;
                _status.Text = "正在停止只读观测，请结束后再关闭探针；保留游戏。";
                return;
            }
            if (_busy) { e.Cancel = true; _status.Text = "操作进行中，最多15秒后结束；不关闭游戏窗口。"; }
        };
        FormClosed += (_, _) => { ResetBinding(); _template?.Dispose(); };
    }

    private void RefreshTargets()
    {
        ResetBinding();
        _ready.Checked = false;
        _targets.Items.Clear();
        foreach (var target in LiveTargets.Find()) _targets.Items.Add(target);
        var flashTargets = _targets.Items.OfType<LiveTarget>().Where(t => t.ClassName == "MacromediaFlashPlayerActiveX").ToArray();
        if (flashTargets.Length == 1) _targets.SelectedItem = flashTargets[0];
        _status.Text = _targets.Items.Count == 0 ? "未发现启动器Flash窗口。请先手动进入存档，再刷新。" : "请选择目标。优先尝试Flash ActiveX子窗口，其次浏览器或宿主。";
    }

    /// <summary>原生调用有独立硬截止；若第三方库卡住，只退出本探针进程。</summary>
    private async Task<bool> RunOperationAsync(Func<Task> operation)
    {
        if (_busy) return false;
        _busy = true;
        _observe.Enabled = _observing;
        foreach (var control in new Control[] { _targets, _method, _input, _refresh, _ready, _capture, _click, _learn, _recognize, _region, _ocr }) control.Enabled = false;
        using var watchdog = new System.Threading.Timer(_ => Environment.Exit(3), null, 15000, Timeout.Infinite);
        try { await operation(); return true; }
        catch (OperationCanceledException) { ClearPreview(); _status.Text = "已停止只读观测；保留游戏窗口。"; return false; }
        catch (Exception ex) { ClearPreview(); ResetBinding(); _status.Text = "未通过：" + ex.Message; return false; }
        finally
        {
            _busy = false;
            UpdateControls();
        }
    }

    /// <summary>连续观测期间仅允许停止，防止目标切换或混入诊断点击。</summary>
    private void UpdateControls()
    {
        foreach (var control in new Control[] { _targets, _method, _input, _refresh, _ready, _capture, _saveMenu, _runConfirm, _progressCheck }) control.Enabled = !_busy && !_observing;
        _click.Enabled = _learn.Enabled = _region.Enabled = !_busy && !_observing && _point.HasValue;
        _recognize.Enabled = !_busy && !_observing && _template is not null;
        _ocr.Enabled = !_busy && !_observing && _ocrRegion.HasValue;
        _observe.Enabled = _observing || (!_busy && _ocrRegion.HasValue);
    }

    private async Task CaptureAsync()
    {
        ClearPreview();
        if (!_ready.Checked || _targets.SelectedItem is not LiveTarget target)
            throw new InvalidOperationException("请选择窗口并确认已进入游戏。");
        var size = LiveTargets.RequireVisibleClient(target);
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) throw new InvalidOperationException("当前没有可用前台窗口，请确认桌面未锁定。");
        if (_controller == IntPtr.Zero)
        {
            var input = _input.SelectedIndex == 0 ? 4UL : 2UL;
            _controller = MaaNative.MaaWin32ControllerCreate(target.Handle, _method.SelectedIndex == 0 ? 16UL : 2UL, input, input);
            if (_controller == IntPtr.Zero) throw new InvalidOperationException("无法创建控制器。");
            _bound = target;
            if (MaaNative.MaaControllerSetOption(_controller, 3, [1], 1) == 0)
                throw new InvalidOperationException("无法启用原始尺寸截图。");
            await WaitAsync(MaaNative.MaaControllerPostConnection(_controller), foreground);
        }
        await WaitAsync(MaaNative.MaaControllerPostScreencap(_controller), foreground);
        var image = MaaNative.MaaImageBufferCreate();
        if (image == IntPtr.Zero) throw new InvalidOperationException("无法分配图片缓冲。");
        try
        {
            if (MaaNative.MaaControllerCachedImage(_controller, image) == 0)
                throw new InvalidOperationException("未获得截图。");
            var data = MaaNative.MaaImageBufferGetEncoded(image);
            var length = MaaNative.MaaImageBufferGetEncodedSize(image);
            if (length == 0 || length > 32 * 1024 * 1024 || data == IntPtr.Zero)
                throw new InvalidOperationException("截图编码为空或过大。");
            var bytes = new byte[(int)length];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            using var stream = new MemoryStream(bytes);
            using var decoded = Image.FromStream(stream);
            if (decoded.Size != size || LiveTargets.RequireVisibleClient(target) != size)
                throw new InvalidOperationException("截图与当前客户区尺寸不符，停止点击，需检查DPI或窗口变化。");
            _preview.Image = new Bitmap(decoded);
            _capturedAt = Stopwatch.GetTimestamp();
            _status.Text = $"已读取{size.Width}×{size.Height}画面，未检测到前台焦点改变。请在120秒内选择预览中的无副作用按钮，再点后台单击。";
        }
        finally { MaaNative.MaaImageBufferDestroy(image); }
    }

    /// <summary>将等比预览坐标转换到原始客户区；黑边不可选，不发送游戏输入。</summary>
    private void SelectPoint(Point location)
    {
        if (_busy || _observing || _preview.Image is not Image image) return;
        var scale = Math.Min((double)_preview.ClientSize.Width / image.Width, (double)_preview.ClientSize.Height / image.Height);
        var x = (location.X - (_preview.ClientSize.Width - image.Width * scale) / 2) / scale;
        var y = (location.Y - (_preview.ClientSize.Height - image.Height * scale) / 2) / scale;
        if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) return;
        var selected = new Point((int)x, (int)y);
        if (_regionStart is Point start)
        {
            _regionStart = null;
            var box = Rectangle.FromLTRB(Math.Min(start.X, selected.X), Math.Min(start.Y, selected.Y),
                Math.Max(start.X, selected.X), Math.Max(start.Y, selected.Y));
            if (box.Width < 16 || box.Height < 16)
            {
                _status.Text = "文字区域太小，请重新选择两角。";
                return;
            }
            _ocrRegion = box;
            _ocr.Enabled = true;
            _observe.Enabled = true;
            _status.Text = $"文字区域({box.X},{box.Y},{box.Width},{box.Height})，点击只读识别会重新截图；不发送输入、不保存图片。";
            return;
        }
        _ocrRegion = null;
        _ocr.Enabled = false;
        _observe.Enabled = false;
        _point = selected;
        _click.Enabled = true;
        _learn.Enabled = true;
        _region.Enabled = true;
        _status.Text = $"选择客户区坐标({_point.Value.X}, {_point.Value.Y})。此时尚未发送点击；用“后台单击一次”执行。";
    }

    /// <summary>保存所选按钮周围的小块模板，只驻留内存；切换账号可复用，关闭探针即释放。</summary>
    private void LearnTemplate()
    {
        if (_busy || _point is not Point point || _preview.Image is not Bitmap frame) return;
        if (Stopwatch.GetElapsedTime(_capturedAt) > TimeSpan.FromSeconds(120))
        {
            ClearPreview();
            _status.Text = "预览已过期，请重新截图再采集模板。";
            return;
        }
        var radius = Math.Max(12, (int)Math.Round(frame.Height * 0.03));
        var box = Rectangle.Intersect(new Rectangle(point.X - radius, point.Y - radius, radius * 2, radius * 2),
            new Rectangle(Point.Empty, frame.Size));
        _template?.Dispose();
        _template = frame.Clone(box, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        _templateFrameSize = frame.Size;
        _recognize.Enabled = true;
        _status.Text = "已记住按钮局部图像（仅内存）。识别单击会重新截图，只接受一个匹配。";
    }

    /// <summary>新截图→唯一匹配→定向输入→新截图；不使用人工选点或旧识别结果重复点击。</summary>
    private async Task RecognizeClickAsync()
    {
        if (_template is null || _input.SelectedIndex != 2)
            throw new InvalidOperationException("请先记住按钮图像，并选择DirectChild输入。");
        await CaptureAsync();
        if (_preview.Image is not Image frame || _bound is null) throw new InvalidOperationException("没有有效截图。");
        var target = _bound;
        var size = frame.Size;
        var scaleX = (double)size.Width / _templateFrameSize.Width;
        var scaleY = (double)size.Height / _templateFrameSize.Height;
        if (Math.Abs(scaleX / scaleY - 1) > 0.01 || scaleX is < 0.5 or > 2)
            throw new InvalidOperationException("游戏比例变化过大，需重新采集模板。");
        using var template = new Bitmap(_template, new Size(Math.Max(1, (int)Math.Round(_template.Width * scaleX)),
            Math.Max(1, (int)Math.Round(_template.Height * scaleY))));
        var foreground = GetForegroundWindow();
        using var recognizer = new MaaTemplateRecognizer();
        var box = await recognizer.FindUniqueAsync(frame, template, () =>
        {
            if (LiveTargets.RequireVisibleClient(target) != size || GetForegroundWindow() != foreground)
                throw new InvalidOperationException("识别期间窗口尺寸或前台焦点变化。");
        });
        if (Stopwatch.GetElapsedTime(_capturedAt) > TimeSpan.FromSeconds(2))
            throw new InvalidOperationException("识别画面超过2秒，不发送点击。");
        _point = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
        await ClickOnceAsync(TimeSpan.FromSeconds(2));
        await CaptureAsync();
        _status.Text = $"唯一匹配({box.X},{box.Y},{box.Width},{box.Height})，已后台单击并更新画面；请确认菜单响应。";
    }

    /// <summary>只消费一次新截图中的坐标；框架发送成功仍需新截图确认游戏响应。</summary>
    private Task ClickAsync() => ClickOnceAsync(TimeSpan.FromSeconds(120));

    /// <summary>同一真实帧比较执行器区域与已校准区域，只返回进度和保存布尔值，不发送输入。</summary>
    private async Task ReadDailyProgressAsync()
    {
        await CaptureAsync();
        var target = _bound!;
        var size = _preview.Image!.Size;
        var foreground = GetForegroundWindow();
        void AssertValid()
        {
            if (LiveTargets.RequireVisibleClient(target) != size || GetForegroundWindow() != foreground)
                throw new InvalidOperationException("进度检查期间目标尺寸或前台焦点改变。");
        }
        var root = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
        using var recognizer = await MaaOcrRecognizer.CreateAsync(root, AssertValid);
        await CaptureAsync();
        if (_preview.Image is not Bitmap frame || frame.Size != new Size(950, 600))
            throw new InvalidOperationException("只读进度检查仅支持950×600。");
        var previous = await ReadSceneAsync(frame, new Rectangle(730, 120, 220, 480), recognizer, AssertValid);
        var current = await DailyProgressReader.ReadAsync(frame, recognizer, AssertValid);
        string Progress(DailyScriptProgress? progress) => progress is null ? "未解析" : $"{progress.Completed}/{progress.Total}";
        _status.Text = $"旧区域：标题{previous.TitleCandidates}，进度{Progress(previous.Progress)}，保存{previous.FinalSaveChecked}；"
            + $"执行器共用识别：标题{current.TitleCandidates}，进度{Progress(current.Progress)}，保存{current.FinalSaveChecked}。只读，未发送输入。";
    }
    /// <summary>只检查固定确认用语与按钮候选；不显示原始OCR，不点击确定或运行脚本。</summary>
    private async Task ReadRunConfirmationAsync()
    {
        await CaptureAsync();
        var target = _bound!;
        var size = _preview.Image!.Size;
        var foreground = GetForegroundWindow();
        void AssertValid()
        {
            if (LiveTargets.RequireVisibleClient(target) != size || GetForegroundWindow() != foreground)
                throw new InvalidOperationException("确认识别期间目标尺寸或前台焦点改变。");
        }
        var root = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
        using var recognizer = await MaaOcrRecognizer.CreateAsync(root, AssertValid);
        await CaptureAsync();
        if (_preview.Image is not Bitmap frame || frame.Size != new Size(950, 600))
            throw new InvalidOperationException("只读确认检查仅支持950×600。");
        async Task<IReadOnlyList<OcrLine>> Read(Rectangle region)
        {
            using var crop = frame.Clone(region, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using var enlarged = new Bitmap(crop, new Size(crop.Width * 2, crop.Height * 2));
            return await recognizer.ReadAsync(enlarged, AssertValid);
        }
        string Compact(string text) => string.Concat(text.Normalize(System.Text.NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c)));
        var prompt = await Read(new Rectangle(260, 170, 450, 100));
        var button = await Read(new Rectangle(345, 295, 100, 60));
        var current = prompt.Count(line => DailyRunPrompt.IsExpected(line.Text));
        var old = prompt.Count(line => Compact(line.Text).StartsWith("是否开始运行", StringComparison.Ordinal)
            && Compact(line.Text).Contains("日常操作复制", StringComparison.Ordinal)
            && (Compact(line.Text).EndsWith('?') || Compact(line.Text).EndsWith('？')));
        var confirm = button.Count(line => Compact(line.Text) == "确定");
        _status.Text = $"只读确认：旧提示规则{old}；当前具名提示{current}；确定按钮{confirm}。未发送输入。";
    }
    /// <summary>复用日常的连续同帧布局等待；只显示固定匹配计数与坐标，不发送选档输入。</summary>
    private async Task ReadSaveMenuAsync()
    {
        await CaptureAsync();
        var target = _bound!;
        var size = _preview.Image!.Size;
        var foreground = GetForegroundWindow();
        void AssertValid()
        {
            if (LiveTargets.RequireVisibleClient(target) != size || GetForegroundWindow() != foreground)
                throw new InvalidOperationException("菜单识别期间目标尺寸或前台焦点改变。");
        }
        var root = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
        using var recognizer = await MaaOcrRecognizer.CreateAsync(root, AssertValid);
        var titleCount = 0;
        var frames = 0;
        var result = await DailySaveLayoutFlow.WaitAsync(async _ =>
        {
            await CaptureAsync();
            frames++;
            if (_preview.Image is not Bitmap frame || frame.Size != new Size(950, 600))
                throw new InvalidOperationException("只读菜单检查仅支持950×600。");
            var region = new Rectangle(240, 135, 480, 65);
            using var crop = frame.Clone(region, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using var enlarged = new Bitmap(crop, new Size(crop.Width * 2, crop.Height * 2));
            var lines = await recognizer.ReadAsync(enlarged, AssertValid);
            string Compact(string text) => string.Concat(text.Normalize(System.Text.NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c)));
            var matches = lines.Where(line => DailyMenuGeometry.IsSaveDialogTitle(Compact(line.Text))).ToArray();
            titleCount = matches.Length;
            if (matches.Length > 1) throw new InvalidOperationException("存档标题不唯一，停止检查。");
            if (matches.Length == 0) return null;
            var box = matches[0].Bounds;
            var title = new Rectangle(region.X + box.X / 2, region.Y + box.Y / 2,
                Math.Max(1, (box.Width + 1) / 2), Math.Max(1, (box.Height + 1) / 2));
            try
            {
                var slot = DailyMenuGeometry.FindSaveSlot(frame, title, 5);
                return new DailySaveSlot(slot.X, slot.Y);
            }
            catch (InvalidOperationException) { return null; }
        }, TimeSpan.FromSeconds(20), default);
        _status.Text = $"只读检查：读取存档标题{titleCount}；采样{frames}帧，连续两帧八卡片一致；存档5坐标({result.X},{result.Y})。未发送输入。";
    }
    /// <summary>人工限定侧栏区域后加载模型并取新截图；结果只在窗口显示，不自动关闭游戏。</summary>
    private async Task ReadRegionAsync()
    {
        if (!_ready.Checked || _ocrRegion is not Rectangle region || _preview.Image is not Image previous || _bound is null)
            throw new InvalidOperationException("请先截图并选择文字区域两角。");
        var target = _bound;
        var size = previous.Size;
        var foreground = GetForegroundWindow();
        void AssertValid()
        {
            if (foreground == IntPtr.Zero || LiveTargets.RequireVisibleClient(target) != size || GetForegroundWindow() != foreground)
                throw new InvalidOperationException("识别期间目标尺寸或前台焦点改变，停止识别。");
        }
        var automationRoot = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
        using var recognizer = await MaaOcrRecognizer.CreateAsync(automationRoot, AssertValid);
        await CaptureAsync();
        AssertValid();
        if (_preview.Image is not Bitmap frame || frame.Size != size)
            throw new InvalidOperationException("新截图尺寸变化，请重新选择文字区域。");
        var scene = await ReadSceneAsync(frame, region, recognizer, AssertValid);
        AssertValid();
        if (Stopwatch.GetElapsedTime(_capturedAt) > TimeSpan.FromSeconds(2))
            throw new InvalidOperationException("识别结果已过期，不用于进度判断。");
        _status.Text = DescribeScene(scene);
    }

    /// <summary>同一帧中提取进度与独立保存勾形，不持有图片或第三方缓冲到下一轮。</summary>
    private static Task<DailyProgressScene> ReadSceneAsync(Bitmap frame, Rectangle region, MaaOcrRecognizer recognizer, Action assertValid)
        => DailyProgressReader.ReadAsync(frame, recognizer, assertValid, region);
    private static string DescribeScene(DailyProgressScene scene)
    {
        var progress = scene.Progress;
        var summary = progress is null ? "未识别到唯一完整进度" : $"进度{progress.Completed}/{progress.Total}";
        // 单帧证据不接完成门槛，不把这次看到的旧完成画面用于自动关闭。
        var save = !scene.FinalSaveChecked ? "未确认保存行绿勾" : "保存行及独立绿色勾形匹配（仅单帧）";
        return summary + "；" + save + "；未判定持久化。绿色区域文字：" + scene.GreenText + "。形状诊断：" + scene.Diagnostic;
    }

    /// <summary>手动限定目标与区域后连续只读；新身份需看见实际进度增长，不执行任何游戏输入。</summary>
    private async Task ObserveLoopAsync()
    {
        if (_busy || !_ready.Checked || _ocrRegion is not Rectangle region || _preview.Image is not Image previous || _bound is null) return;
        var target = _bound;
        var size = previous.Size;
        var clockStart = Stopwatch.GetTimestamp();
        // 仅探针参数：间隔1秒，3样本且至少3秒稳定；真实产品阈值仍待过程验收。
        var observer = new DailyScriptRunObserver(Guid.NewGuid(), 3, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
        _observing = true;
        _observationCancellation = new CancellationTokenSource();
        var cancellation = _observationCancellation.Token;
        _observe.Text = "停止只读观测";
        UpdateControls();
        MaaOcrRecognizer? recognizer = null;
        var frames = 0;
        var transitions = new Queue<string>();
        string? lastSummary = null;
        _trace.Clear();
        void AssertTarget()
        {
            if (LiveTargets.RequireVisibleClient(target) != size || GetForegroundWindow() == IntPtr.Zero)
                throw new InvalidOperationException("目标尺寸变化、不可见或桌面不可用，停止观测。");
        }
        try
        {
            if (!await RunOperationAsync(async () => recognizer = await MaaOcrRecognizer.CreateAsync(
                Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName, AssertTarget))) return;
            while (!cancellation.IsCancellationRequested && Stopwatch.GetElapsedTime(clockStart) < TimeSpan.FromMinutes(60))
            {
                var complete = false;
                if (!await RunOperationAsync(async () =>
                {
                    AssertTarget();
                    await CaptureAsync();
                    if (_bound != target || _preview.Image is not Bitmap frame || frame.Size != size)
                        throw new InvalidOperationException("观测绑定变化，停止当前运行。");
                    var scene = await ReadSceneAsync(frame, region, recognizer!, AssertTarget);
                    AssertTarget();
                    cancellation.ThrowIfCancellationRequested();
                    if (Stopwatch.GetElapsedTime(_capturedAt) > TimeSpan.FromSeconds(2))
                        throw new InvalidOperationException("观测截图过期，停止当前运行。");
                    var result = observer.Observe(Stopwatch.GetElapsedTime(clockStart, _capturedAt), Stopwatch.GetElapsedTime(clockStart),
                        scene.Progress, scene.FinalSaveChecked, scene.HasFailedItems);
                    frames++;
                    complete = result.State == DailyScriptCompletionState.VisualComplete;
                    // 数字摘要区分“标题未识别”和“采样未看见增长”，不放宽业务门槛。
                    var summary = $"{result.State}；标题候选{scene.TitleCandidates}；进度"
                        + (scene.Progress is null ? "未解析" : $"{scene.Progress.Completed}/{scene.Progress.Total}")
                        + $"；保存勾形{scene.FinalSaveChecked}";
                    if (summary != lastSummary)
                    {
                        lastSummary = summary;
                        transitions.Enqueue($"第{frames}帧/{Stopwatch.GetElapsedTime(clockStart).TotalSeconds:F1}秒：{summary}");
                        while (transitions.Count > 12) transitions.Dequeue();
                        _trace.Text = string.Join(Environment.NewLine, transitions);
                    }
                    _status.Text = $"连续只读第{frames}帧：{result.State}；" + DescribeScene(scene)
                        + (result.HasFailedItems ? "；观察到中间红叉（仅提示）" : "");
                })) return;
                if (complete) return;
                await Task.Delay(1000, cancellation);
            }
            _status.Text = cancellation.IsCancellationRequested ? "已停止只读观测；保留游戏窗口。" : "观测达到60分钟上限；保留游戏窗口。";
        }
        catch (OperationCanceledException) { _status.Text = "已停止只读观测；保留游戏窗口。"; }
        finally
        {
            // OCR销毁也可能阻塞，仅退出探针；不终止游戏或留下常驻观测任务。
            using var watchdog = new System.Threading.Timer(_ => Environment.Exit(3), null, 5000, Timeout.Infinite);
            recognizer?.Dispose();
            _observationCancellation.Dispose();
            _observationCancellation = null;
            _observing = false;
            _observe.Text = "连续只读观测";
            UpdateControls();
        }
    }



    private async Task ClickOnceAsync(TimeSpan maximumAge)
    {
        if (!_ready.Checked || _bound is null || _point is not Point point || _preview.Image is null)
            throw new InvalidOperationException("需要重新截图并选择位置。");
        if (Stopwatch.GetElapsedTime(_capturedAt) > maximumAge)
            throw new InvalidOperationException("截图已过期，请重新截图；不使用过期坐标。");
        if (LiveTargets.RequireVisibleClient(_bound) != _preview.Image.Size)
            throw new InvalidOperationException("窗口尺寸改变，请重新截图。");
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || !GetCursorPos(out var before))
            throw new InvalidOperationException("无法确认当前焦点或鼠标位置，停止发送点击。");
        ClearPreview(); // 一次性坐标，即使操作失败也不自动重试。
        if (_input.SelectedIndex == 2)
        {
            await DirectChildInput.ClickAsync(_bound, point, () =>
            {
                if (GetForegroundWindow() != foreground || !GetCursorPos(out var current) || current != before)
                    throw new InvalidOperationException("前台焦点或鼠标位置变化，本次不通过。");
                if (Stopwatch.GetElapsedTime(_capturedAt) > maximumAge)
                    throw new InvalidOperationException("输入开始前截图过期；不使用旧坐标。");
            });
        }
        else
        {
            await WaitAsync(MaaNative.MaaControllerPostClick(_controller, point.X, point.Y), foreground);
        }
        if (!GetCursorPos(out var after))
            throw new InvalidOperationException("点击已发送，但无法确认鼠标位置；本次不能认定不抢操作。");
        _status.Text = before == after
            ? $"已发送一次{_input.SelectedItem}，未检测到焦点/鼠标变化。请重新截图确认游戏是否响应；发送成功不等于游戏处理成功。"
            : "已发送一次点击，但鼠标位置变化（也可能是手动移动）。本次不能认定不抢操作。";
    }

    private async Task WaitAsync(long id, IntPtr foreground)
    {
        if (id == 0) throw new InvalidOperationException("Maa拒绝请求。");
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (_bound is null) throw new InvalidOperationException("未绑定目标。");
            LiveTargets.RequireVisibleClient(_bound);
            if (GetForegroundWindow() != foreground) throw new InvalidOperationException("前台焦点发生变化，本次不通过。");
            var status = MaaNative.MaaControllerStatus(_controller, id);
            if (status == 3000) return;
            if (status is 0 or 4000) throw new InvalidOperationException($"Maa返回失败状态{status}。");
            await Task.Delay(30);
        }
        throw new TimeoutException("操作超过8秒；不会切换前台重试。");
    }

    private void ClearPreview()
    {
        _point = null;
        _regionStart = null;
        _ocrRegion = null;
        _region.Enabled = _ocr.Enabled = false;
        _observe.Enabled = _observing;
        _click.Enabled = false;
        _learn.Enabled = false;
        var old = _preview.Image;
        _preview.Image = null;
        old?.Dispose();
    }

    private void ResetBinding()
    {
        ClearPreview();
        if (_controller != IntPtr.Zero)
        {
            using var watchdog = new System.Threading.Timer(_ => Environment.Exit(3), null, 5000, Timeout.Infinite);
            MaaNative.MaaControllerDestroy(_controller);
            _controller = IntPtr.Zero;
        }
        _bound = null;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);
}
