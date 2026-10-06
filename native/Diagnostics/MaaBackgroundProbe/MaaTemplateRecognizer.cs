using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MaaBackgroundProbe;

/// <summary>只做内存图片模板识别，不绑定输入控制器；结果必须唯一才允许调用方点击。</summary>
internal sealed class MaaTemplateRecognizer : IDisposable
{
    // 官方C接口的字符串必须UTF-8且包含结束零字节，避免系统代码页改变语义。
    private static readonly byte[] ImageName = Encoding.UTF8.GetBytes("probe-button.png\0");
    private static readonly byte[] Algorithm = Encoding.UTF8.GetBytes("TemplateMatch\0");
    private static readonly byte[] Parameters = Encoding.UTF8.GetBytes("{\"template\":[\"probe-button.png\"],\"threshold\":[0.92],\"method\":5}\0");
    private IntPtr _resource;
    private IntPtr _tasker;

    public MaaTemplateRecognizer()
    {
        _resource = MaaNative.MaaResourceCreate();
        _tasker = MaaNative.MaaTaskerCreate();
        if (_resource == IntPtr.Zero || _tasker == IntPtr.Zero || MaaNative.MaaTaskerBindResource(_tasker, _resource) == 0)
        {
            Dispose();
            throw new InvalidOperationException("无法初始化模板识别器。");
        }
    }

    /// <summary>在新的完整截图上查找模板；不匹配或存在多个候选均拒绝返回点击坐标。</summary>
    public async Task<Rectangle> FindUniqueAsync(Image frame, Image template, Action assertValid)
    {
        ObjectDisposedException.ThrowIf(_tasker == IntPtr.Zero, this);
        if (MaaNative.MaaTaskerClearCache(_tasker) == 0) throw new InvalidOperationException("无法清理上一轮识别缓存。");
        var frameBuffer = Encode(frame);
        var templateBuffer = IntPtr.Zero;
        var text = IntPtr.Zero;
        var name = IntPtr.Zero;
        var algorithm = IntPtr.Zero;
        try
        {
            templateBuffer = Encode(template);
            if (MaaNative.MaaResourceOverrideImage(_resource, ImageName, templateBuffer) == 0)
                throw new InvalidOperationException("无法加载内存模板。");
            var id = MaaNative.MaaTaskerPostRecognition(_tasker, Algorithm, Parameters, frameBuffer);
            if (id == 0) throw new InvalidOperationException("框架拒绝模板识别。");
            var timer = Stopwatch.StartNew();
            while (MaaNative.MaaTaskerStatus(_tasker, id) is 1000 or 2000)
            {
                assertValid();
                if (timer.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("模板识别超过8秒。");
                await Task.Delay(25);
            }
            assertValid();
            if (MaaNative.MaaTaskerStatus(_tasker, id) != 3000)
                throw new InvalidOperationException("模板识别未完成或未找到目标。");
            text = MaaNative.MaaStringBufferCreate();
            name = MaaNative.MaaStringBufferCreate();
            algorithm = MaaNative.MaaStringBufferCreate();
            if (text == IntPtr.Zero || name == IntPtr.Zero || algorithm == IntPtr.Zero)
                throw new InvalidOperationException("无法分配识别结果缓冲。");
            var nodes = new long[16];
            ulong count = (ulong)nodes.Length;
            if (MaaNative.MaaTaskerGetTaskDetail(_tasker, id, name, nodes, ref count, out _) == 0 || count != 1
                || MaaNative.MaaTaskerGetNodeDetail(_tasker, nodes[0], name, out var recoId, out _, out _) == 0
                || MaaNative.MaaTaskerGetRecognitionDetail(_tasker, recoId, name, algorithm, out var hit, out var box, text, IntPtr.Zero, IntPtr.Zero) == 0)
                throw new InvalidOperationException("无法读取单次识别结果。");
            var json = Marshal.PtrToStringUTF8(MaaNative.MaaStringBufferGet(text)) ?? "{}";
            using var result = JsonDocument.Parse(json);
            if (hit == 0 || !result.RootElement.TryGetProperty("filtered", out var filtered) || filtered.GetArrayLength() != 1)
                throw new InvalidOperationException("模板未找到唯一匹配；不会发送输入。");
            var rectangle = new Rectangle(box.X, box.Y, box.Width, box.Height);
            if (rectangle.Width <= 0 || rectangle.Height <= 0 || !new Rectangle(Point.Empty, frame.Size).Contains(rectangle))
                throw new InvalidOperationException("识别坐标超出当前画面。");
            return rectangle;
        }
        finally
        {
            if (text != IntPtr.Zero) MaaNative.MaaStringBufferDestroy(text);
            if (name != IntPtr.Zero) MaaNative.MaaStringBufferDestroy(name);
            if (algorithm != IntPtr.Zero) MaaNative.MaaStringBufferDestroy(algorithm);
            if (templateBuffer != IntPtr.Zero) MaaNative.MaaImageBufferDestroy(templateBuffer);
            MaaNative.MaaImageBufferDestroy(frameBuffer);
        }
    }

    private static IntPtr Encode(Image image)
    {
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        var bytes = stream.ToArray();
        var buffer = MaaNative.MaaImageBufferCreate();
        if (buffer == IntPtr.Zero) throw new InvalidOperationException("无法分配图片缓冲。");
        if (MaaNative.MaaImageBufferSetEncoded(buffer, bytes, (ulong)bytes.Length) == 0)
        {
            MaaNative.MaaImageBufferDestroy(buffer);
            throw new InvalidOperationException("无法解码内存图片。");
        }
        return buffer;
    }

    /// <summary>先释放任务线程，再释放其资源；调用方保证没有运行中的识别。</summary>
    public void Dispose()
    {
        if (_tasker != IntPtr.Zero) { MaaNative.MaaTaskerDestroy(_tasker); _tasker = IntPtr.Zero; }
        if (_resource != IntPtr.Zero) { MaaNative.MaaResourceDestroy(_resource); _resource = IntPtr.Zero; }
    }
}
