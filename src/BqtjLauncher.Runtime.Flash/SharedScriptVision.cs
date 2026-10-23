using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using BqtjLauncher.Application;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>共享原生视觉进程，串行请求不串结果；模型只加载一次，最后一个租用者退出时释放进程。</summary>
public sealed class SharedScriptVision(string componentDirectory) : IScriptVision, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _worker;
    private int _leases;
    private long _requestId;

    /// <summary>刷关输入要求协议3；日常/读名继续兼容已有协议2组件。</summary>
    public bool SupportsBattleInput => HasProtocol(3);
    public bool SupportsDiagonalInput => HasProtocol(4);
    public bool SupportsBattleAi => HasProtocol(5);
    public bool SupportsBattleAiRise => HasProtocol(6);
    public bool SupportsQuotaCounter => HasProtocol(7);

    private bool HasProtocol(int minimum)
    {
        var path = Path.Combine(componentDirectory, "component.json");
        if (!File.Exists(path)) return false;
        using var metadata = JsonDocument.Parse(File.ReadAllText(path));
        return metadata.RootElement.TryGetProperty("protocolVersion", out var version) && version.GetInt32() >= minimum;
    }

    public async Task BeginAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_worker is null)
            {
                if (!Environment.Is64BitOperatingSystem) throw new NotSupportedException("脚本视觉组件需要64位Windows。");
                var path = Path.Combine(componentDirectory, "BqtjScriptVision.exe");
                if (!File.Exists(path)) throw new InvalidOperationException("缺少视觉组件，请将组件包解压到启动器同目录。");
                // 名称的黄色小字识别是协议2能力；旧组件提前明确提示更新，避免选档阶段才报未知指令。
                using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(componentDirectory, "component.json")));
                if (!metadata.RootElement.TryGetProperty("protocolVersion", out var protocol) || protocol.GetInt32() < 2)
                    throw new InvalidOperationException("脚本视觉组件版本过旧，请解压本轮配套组件包，覆盖components目录。");
                var info = new ProcessStartInfo(path)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = componentDirectory,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true
                };
                info.ArgumentList.Add(componentDirectory);
                _worker = Process.Start(info) ?? throw new InvalidOperationException("无法启动视觉组件。");
                try
                {
                    var line = await _worker.StandardOutput.ReadLineAsync(token).AsTask()
                        .WaitAsync(TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
                    if (line != "ready") throw new InvalidOperationException("视觉组件初始化失败，请检查组件是否完整。");
                }
                catch { StopWorker(); throw; }
            }
            if (_worker.HasExited) throw new InvalidOperationException("视觉组件已退出，请停止队列后重新运行。");
            _leases++;
        }
        finally { _gate.Release(); }
    }

    public async Task EndAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (_leases > 0 && --_leases == 0) StopWorker(); }
        finally { _gate.Release(); }
    }

    public async Task<ScriptVisionFrame> ReadAsync(AutomationWindowTarget target, ScriptRect region, CancellationToken token,
        long snapshotId = 0, ScriptTextStyle style = ScriptTextStyle.Normal)
    {
        // 菜单小字沿用原型的2倍放大；已校准的日常侧栏保持原尺寸，完成标记使用同帧原始像素。
        var scale = style is ScriptTextStyle.SaveName or ScriptTextStyle.Green or ScriptTextStyle.QuotaCounter ? 3 : region == new ScriptRect(759, 154, 182, 420) ? 1 : 2;
        var operation = style switch
        {
            ScriptTextStyle.Green => "green",
            ScriptTextStyle.SaveName => "save-name",
            ScriptTextStyle.QuotaCounter => "quota-counter",
            _ => scale == 2 ? "ocr2" : "ocr"
        };
        var response = await RequestAsync(operation, target, region, snapshotId, null, token).ConfigureAwait(false);
        var lines = new List<ScriptText>();
        using var json = JsonDocument.Parse(response.Json);
        var result = style == ScriptTextStyle.SaveName ? SelectSaveNameVariant(json.RootElement) : json.RootElement;
        if (result.TryGetProperty("filtered", out var filtered))
            foreach (var line in filtered.EnumerateArray())
            {
                var box = line.GetProperty("box");
                lines.Add(new(line.GetProperty("text").GetString() ?? "",
                    new(box[0].GetInt32() / scale + region.X, box[1].GetInt32() / scale + region.Y,
                        Math.Max(1, (box[2].GetInt32() + scale - 1) / scale),
                        Math.Max(1, (box[3].GetInt32() + scale - 1) / scale)), line.GetProperty("score").GetDouble()));
            }
        return new(snapshotId == 0 ? response.Id : snapshotId, response.CapturedAt, lines);
    }

    /// <summary>按字符数加权比较同帧双底色OCR；分差不足两百分点时保留白底，避免微小波动翻转相近汉字。</summary>
    internal static JsonElement SelectSaveNameVariant(JsonElement result)
    {
        var light = result.GetProperty("light");
        var dark = result.GetProperty("dark");
        static double Score(JsonElement variant)
        {
            var weight = 0; var sum = 0d;
            foreach (var line in variant.GetProperty("filtered").EnumerateArray())
            {
                var count = (line.GetProperty("text").GetString() ?? "").Length;
                weight += count; sum += count * line.GetProperty("score").GetDouble();
            }
            return weight == 0 ? 0 : sum / weight;
        }
        return Score(dark) >= Score(light) + 0.02 ? dark : light;
    }
    /// <summary>协议已有pixels可直接拍新快照；拾取不再为了取图先跑一遍OCR。</summary>
    public async Task<ScriptSceneFrame> CaptureAsync(AutomationWindowTarget target, CancellationToken token)
    {
        var full = new ScriptRect(0, 0, 950, 600);
        var response = await RequestAsync("pixels", target, full, 0, null, token).ConfigureAwait(false);
        return new(new(response.Id, response.CapturedAt, []), DecodePixels(response, full));
    }

    public async Task<ScriptPixels> PixelsAsync(AutomationWindowTarget target, ScriptRect region, long snapshotId, CancellationToken token)
    {
        var response = await RequestAsync("pixels", target, region, snapshotId, null, token).ConfigureAwait(false);
        return DecodePixels(response, region);
    }

    private static ScriptPixels DecodePixels(Reply response, ScriptRect region)
    {
        var pixels = Convert.FromHexString(response.Pixels ?? throw new InvalidOperationException("截图像素缺失。"));
        if (pixels.Length != region.Width * region.Height * 3) throw new InvalidOperationException("截图像素尺寸无效。");
        return new(region.Width, region.Height, pixels);
    }

    public async Task<ScriptRect?> FindImageAsync(AutomationWindowTarget target, ScriptRect region, byte[] image, CancellationToken token)
    {
        var matches = await FindImagesAsync(target, region, image, 0, token).ConfigureAwait(false);
        if (matches.Count > 1) throw new InvalidOperationException("图片有多个匹配，未发送点击。");
        return matches.Count == 0 ? null : matches[0];
    }

    public async Task<IReadOnlyList<ScriptRect>> FindImagesAsync(AutomationWindowTarget target, ScriptRect region, byte[] image, long snapshotId, CancellationToken token,
        ScriptImageAlgorithm algorithm = ScriptImageAlgorithm.Template)
    {
        var scale = algorithm == ScriptImageAlgorithm.Features ? 3 : 1;
        var response = await RequestAsync(scale == 3 ? "feature-image" : "image", target, region, snapshotId, Convert.ToHexString(image), token).ConfigureAwait(false);
        using var json = JsonDocument.Parse(response.Json);
        if (!json.RootElement.TryGetProperty("filtered", out var matches)) return [];
        return matches.EnumerateArray().Select(match =>
        {
            var box = match.GetProperty("box");
            return new ScriptRect(box[0].GetInt32() / scale + region.X, box[1].GetInt32() / scale + region.Y,
                Math.Max(1, (box[2].GetInt32() + scale - 1) / scale), Math.Max(1, (box[3].GetInt32() + scale - 1) / scale));
        }).ToArray();
    }

    public async Task ClickAsync(AutomationWindowTarget target, int x, int y, CancellationToken token)
        => _ = await RequestAsync("click", target, new(x, y, 0, 0), 0, null, token).ConfigureAwait(false);

    public async Task MovePointerAsync(AutomationWindowTarget target, int x, int y, CancellationToken token)
        => _ = await RequestAsync("hover", target, new(x, y, 0, 0), 0, null, token).ConfigureAwait(false);

    /// <summary>短脉冲回执代表按键已释放；取消时等待回执再交还窗口，防止遗留移动状态。</summary>
    public async Task HoldKeyAsync(AutomationWindowTarget target, char key, int milliseconds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!SupportsBattleInput) throw new NotSupportedException("请更新协议3脚本组件。");
        await RequestAsync("key", target, new(char.ToUpperInvariant(key), 0, milliseconds, 0), 0, null, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    /// <summary>组合键一次请求完成，取消同样等待两键全部释放，不能拆成两个串行脉冲。</summary>
    public async Task HoldKeysAsync(AutomationWindowTarget target, char first, char second, int milliseconds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!SupportsDiagonalInput) throw new NotSupportedException("斜线移动需要协议4脚本组件。");
        await RequestAsync("keys", target, new(char.ToUpperInvariant(first), char.ToUpperInvariant(second), milliseconds, 0), 0, null, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    public async Task ReleaseTargetAsync(AutomationWindowTarget target)
    {
        try { await RequestAsync("release", target, default, 0, null, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or OperationCanceledException) { }
    }

    public async Task ToggleBattleAiAsync(AutomationWindowTarget target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!SupportsBattleAi) throw new NotSupportedException("AI切换需要协议5脚本组件。");
        await RequestAsync("battle-ai", target, new('N', 0, 50, 0), 0, null, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    /// <summary>N与W必须在原生端同时按下，不能把两个串行管道请求伪装成同时输入。</summary>
    public async Task ToggleBattleAiAndRiseAsync(AutomationWindowTarget target, int milliseconds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!SupportsBattleAiRise) throw new NotSupportedException("同步关闭AI并上升需要协议6脚本组件。");
        await RequestAsync("battle-ai-rise", target, new('N', 'W', milliseconds, 0), 0, null, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    private sealed record Reply(long Id, long CapturedAt, string Json, string? Pixels);

    /// <summary>请求编号匹配回执；取消或通信超时终止视觉进程，避免迟到输入在下次任务中执行。</summary>
    private async Task<Reply> RequestAsync(string command, AutomationWindowTarget target, ScriptRect region,
        long snapshot, string? extra, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_worker is null || _worker.HasExited) throw new InvalidOperationException("视觉组件已退出，游戏窗口保持不变。");
            var id = ++_requestId;
            var line = FormattableString.Invariant($"{command} {id} {target.NativeProcessId} {target.ContainerProcessId} {target.FlashWindowHandle} {region.X} {region.Y} {region.Width} {region.Height} {snapshot}");
            if (extra is not null) line += " " + extra;
            // 按键最多500ms，发送后必须等原生端finally发出KEYUP，不能因取消直接杀进程。
            token.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(command is "key" or "keys" or "battle-ai" or "battle-ai-rise" ? CancellationToken.None : token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await _worker.StandardInput.WriteLineAsync(line.AsMemory(), timeout.Token).ConfigureAwait(false);
                await _worker.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
                var reply = (await _worker.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false))?.Split(' ')
                    ?? throw new IOException("视觉组件连接已关闭。");
                if (reply.Length < 4 || reply[0] != "reply" || reply[1] != id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    throw new IOException("视觉组件回执不匹配。");
                if (reply[2] == "error")
                {
                    throw CreateFailure(reply[3]);
                }
                if (reply.Length < 5) throw new IOException("视觉回执不完整。");
                return new(id, long.Parse(reply[3], System.Globalization.CultureInfo.InvariantCulture),
                    Encoding.UTF8.GetString(Convert.FromHexString(reply[4])), reply.Length > 5 ? reply[5] : null);
            }
            catch (OperationCanceledException) { StopWorker(); throw; }
            catch (IOException) { StopWorker(); throw; }
        }
        finally { _gate.Release(); }
    }

    /// <summary>保留原有异常类别及提示，另附固定原生错误码供诊断；未知正文不写入日志。</summary>
    internal static InvalidOperationException CreateFailure(string code)
    {
        InvalidOperationException error = code is "target-hidden" or "target-size"
            ? new ScriptTargetUnavailableException("游戏窗口最小化、隐藏或尺寸变化；等待950×600画面恢复后继续")
            : new InvalidOperationException(code switch
            {
                "input-uncertain" => "输入回执不确定，已暂停；不会自动重复输入。",
                "target-changed" => "本次游戏窗口已退出或被替换。",
                "snapshot" => "本次截图已被替换，请重新观察。",
                "controller-connect" => "视觉组件未能连接游戏画面，已暂停并保留窗口。",
                "capture" => "未能取得游戏截图，已暂停并保留窗口。",
                "recognition" => "画面识别任务失败，已暂停并保留窗口。",
                _ => "视觉操作失败，已暂停并保留游戏窗口。"
            });
        error.Data["ScriptVisionCode"] = code is "capture" or "controller-connect" or "component" or "component-api" or "component-cpu" or "coordinates"
            or "image" or "input-uncertain" or "protocol" or "recognition" or "recognition-detail" or "snapshot"
            or "target-changed" or "target-hidden" or "target-size" or "vision" or "vision-timeout" ? code : "unknown";
        return error;
    }

    private void StopWorker()
    {
        if (_worker is null) return;
        try
        {
            if (!_worker.HasExited)
            {
                _worker.StandardInput.Close();
                if (!_worker.WaitForExit(2000)) _worker.Kill(entireProcessTree: true);
            }
        }
        finally { _worker.Dispose(); _worker = null; }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { StopWorker(); _leases = 0; }
        finally { _gate.Release(); }
    }
}
