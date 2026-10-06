using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MaaBackgroundProbe;

/// <summary>一条局部文字识别，坐标属于本次输入图片；文字本身不表示保存绿勾。</summary>
internal sealed record OcrLine(string Text, Rectangle Bounds, double Confidence);

/// <summary>独立中文OCR实例，只处理调用方提供的图片，不绑定窗口或输入控制器。</summary>
internal sealed class MaaOcrRecognizer : IDisposable
{
    private static readonly byte[] Algorithm = Encoding.UTF8.GetBytes("OCR\0");
    // 当前为探针阈值，不能据此宣称真实游戏的完成识别已经验收。
    private static readonly byte[] Parameters = Encoding.UTF8.GetBytes("{\"threshold\":0.85,\"order_by\":\"Vertical\"}\0");
    private IntPtr _resource;
    private IntPtr _tasker;
    private int _busy;

    private MaaOcrRecognizer()
    {
        _resource = MaaNative.MaaResourceCreate();
        _tasker = MaaNative.MaaTaskerCreate();
        if (_resource == IntPtr.Zero || _tasker == IntPtr.Zero || MaaNative.MaaTaskerBindResource(_tasker, _resource) == 0)
        {
            Dispose();
            throw new InvalidOperationException("无法初始化OCR实例。");
        }
    }

    /// <summary>加载前核对嵌入的固定模型清单，拒绝损坏、缺失和重解析目录。</summary>
    public static async Task<MaaOcrRecognizer> CreateAsync(string automationDirectory, Action assertValid)
    {
        using var manifestStream = typeof(MaaOcrRecognizer).Assembly.GetManifestResourceStream("MaaBackgroundProbe.OcrModels.json")!;
        using var manifest = JsonDocument.Parse(manifestStream);
        var root = Path.Combine(automationDirectory, manifest.RootElement.GetProperty("directory").GetString()!);
        foreach (var entry in manifest.RootElement.GetProperty("files").EnumerateObject())
        {
            var path = Path.Combine(root, "model", "ocr", entry.Name);
            for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("OCR路径包含重解析点。");
            using var file = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(entry.Value.GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("OCR模型校验失败，不加载模型。");
        }
        assertValid();
        var recognizer = new MaaOcrRecognizer();
        try
        {
            var id = MaaNative.MaaResourcePostBundle(recognizer._resource, Encoding.UTF8.GetBytes(root + '\0'));
            await WaitAsync(() => MaaNative.MaaResourceStatus(recognizer._resource, id), id, assertValid);
            return recognizer;
        }
        catch { recognizer.Dispose(); throw; }
    }

    /// <summary>只识别所传局部画面，每轮清缓存；同一实例不允许并行混用结果。</summary>
    public async Task<IReadOnlyList<OcrLine>> ReadAsync(Image frame, Action assertValid)
    {
        ObjectDisposedException.ThrowIf(_tasker == IntPtr.Zero, this);
        if (Interlocked.Exchange(ref _busy, 1) != 0) throw new InvalidOperationException("同一OCR实例正在识别。");
        var image = IntPtr.Zero;
        var name = IntPtr.Zero;
        var algorithm = IntPtr.Zero;
        var detail = IntPtr.Zero;
        try
        {
            assertValid();
            if (MaaNative.MaaTaskerClearCache(_tasker) == 0) throw new InvalidOperationException("无法清理OCR缓存。");
            using var stream = new MemoryStream();
            frame.Save(stream, ImageFormat.Png);
            var bytes = stream.ToArray();
            image = MaaNative.MaaImageBufferCreate();
            if (image == IntPtr.Zero || MaaNative.MaaImageBufferSetEncoded(image, bytes, (ulong)bytes.Length) == 0)
                throw new InvalidOperationException("无法加载局部OCR画面。");
            var id = MaaNative.MaaTaskerPostRecognition(_tasker, Algorithm, Parameters, image);
            await WaitAsync(() => MaaNative.MaaTaskerStatus(_tasker, id), id, assertValid);
            name = MaaNative.MaaStringBufferCreate();
            algorithm = MaaNative.MaaStringBufferCreate();
            detail = MaaNative.MaaStringBufferCreate();
            if (name == IntPtr.Zero || algorithm == IntPtr.Zero || detail == IntPtr.Zero)
                throw new InvalidOperationException("无法分配OCR结果缓冲。");
            var nodes = new long[16];
            ulong count = (ulong)nodes.Length;
            if (MaaNative.MaaTaskerGetTaskDetail(_tasker, id, name, nodes, ref count, out _) == 0 || count != 1
                || MaaNative.MaaTaskerGetNodeDetail(_tasker, nodes[0], name, out var recoId, out _, out _) == 0
                || MaaNative.MaaTaskerGetRecognitionDetail(_tasker, recoId, name, algorithm, out _, out _, detail, IntPtr.Zero, IntPtr.Zero) == 0)
                throw new InvalidOperationException("无法读取单次OCR结果。");
            using var json = JsonDocument.Parse(Marshal.PtrToStringUTF8(MaaNative.MaaStringBufferGet(detail)) ?? "{}");
            var lines = new List<OcrLine>();
            foreach (var item in json.RootElement.GetProperty("filtered").EnumerateArray())
            {
                var box = item.GetProperty("box");
                var bounds = new Rectangle(box[0].GetInt32(), box[1].GetInt32(), box[2].GetInt32(), box[3].GetInt32());
                var confidence = item.GetProperty("score").GetDouble();
                if (bounds.Width <= 0 || bounds.Height <= 0 || !new Rectangle(Point.Empty, frame.Size).Contains(bounds)
                    || !double.IsFinite(confidence) || confidence < 0.85 || confidence > 1)
                    throw new InvalidOperationException("OCR结果坐标或置信度不符。");
                lines.Add(new OcrLine(item.GetProperty("text").GetString()!, bounds, confidence));
            }
            return lines;
        }
        finally
        {
            if (detail != IntPtr.Zero) MaaNative.MaaStringBufferDestroy(detail);
            if (algorithm != IntPtr.Zero) MaaNative.MaaStringBufferDestroy(algorithm);
            if (name != IntPtr.Zero) MaaNative.MaaStringBufferDestroy(name);
            if (image != IntPtr.Zero) MaaNative.MaaImageBufferDestroy(image);
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static async Task WaitAsync(Func<int> status, long id, Action assertValid)
    {
        if (id == 0) throw new InvalidOperationException("Maa拒绝OCR操作。");
        var timer = Stopwatch.StartNew();
        while (true)
        {
            assertValid();
            var current = status();
            if (current == 3000) return;
            if (current is not (1000 or 2000)) throw new InvalidOperationException("OCR操作失败。");
            if (timer.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("OCR操作超过8秒。");
            await Task.Delay(25);
        }
    }

    /// <summary>调用方保证识别已结束；先销毁任务，再释放模型资源。</summary>
    public void Dispose()
    {
        if (_tasker != IntPtr.Zero) { MaaNative.MaaTaskerDestroy(_tasker); _tasker = IntPtr.Zero; }
        if (_resource != IntPtr.Zero) { MaaNative.MaaResourceDestroy(_resource); _resource = IntPtr.Zero; }
    }
}
