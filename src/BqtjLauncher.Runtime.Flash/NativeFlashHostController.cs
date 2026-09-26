using System.Diagnostics;
using System.Globalization;
using System.IO;
using BqtjLauncher.Domain;

namespace BqtjLauncher.Runtime.Flash;

internal sealed class NativeFlashHostController : IDisposable
{
    /// <summary>宿主最多启动次数：首次卡住后重启一次，避免把偶发卡顿直接变成启动失败。</summary>
    private const int MaximumHostStartAttempts = 2;

    /// <summary>等待宿主第一行输出（进程已起来）的上限。</summary>
    private static readonly TimeSpan HostFirstStageTimeout = TimeSpan.FromSeconds(10);

    /// <summary>IE/Flash 初始化可能受网络影响，收到 stage 后给足时间。</summary>
    private static readonly TimeSpan HostInitializationTimeout = TimeSpan.FromSeconds(45);

    private readonly Uri _gamePageUri;
    private readonly bool _audioControlEnabled;
    private readonly SemaphoreSlim _responseGate = new(1, 1);
    private readonly GameAudioSessionMute _audioMute = new();
    private Process? _process;
    private int _hostWidth;
    private int _hostHeight;
    private decimal _pageScale = 1m;
    private bool _disposed;

    public NativeFlashHostController(Uri gamePageUri, bool audioControlEnabled = true)
    {
        _gamePageUri = gamePageUri;
        _audioControlEnabled = audioControlEnabled;
    }

    public SpeedMultiplier Current { get; private set; } = SpeedMultiplier.Original;

    /// <summary>
    /// 宿主输出行观察口：命令执行期间由 <see cref="ReadHostLineAsync"/> 逐行回调，
    /// 用于确认宿主回执是否真的从管道到达（用户现场没有调试器，只能靠日志）。
    /// </summary>
    internal Action<string>? LineObserver { get; set; }

    /// <summary>只通过已绑定子进程的控制管道传递限长凭据，编码避免换行注入协议。</summary>
    public void ConfigureCredential(BqtjLauncher.Application.AccountCredential? credential)
    {
        if (credential is null) return;
        if (credential.Username.Length > 256 || credential.Password.Length > 1024
            || credential.Username.Contains('\0') || credential.Password.Contains('\0')) return;
        SendCommand("credential " + Convert.ToHexString(System.Text.Encoding.Unicode.GetBytes(credential.Username))
            + ":" + Convert.ToHexString(System.Text.Encoding.Unicode.GetBytes(credential.Password)));
    }

    /// <summary>
    /// 启动原生宿主并等待 ready。宿主在耗时初始化前会先报 stage 行，
    /// 因此这里按“第一次响应”和“初始化完成”分段计时，并允许一次重试：
    /// 首次 ready 超时通常来自 IE/Flash 初始化卡住，重新拉起一个干净进程即可恢复。
    /// </summary>
    /// <param name="reportDetail">启动细节输出，用于写入日志排查现场问题。</param>
    public void Start(
        nint parentHandle,
        int width,
        int height,
        decimal initialScale,
        Action<string>? reportDetail = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is not null)
        {
            throw new InvalidOperationException("原生 Flash 宿主已经启动。");
        }

        var executablePath = Path.Combine(AppContext.BaseDirectory, "BqtjNativeFlashHost.exe");
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                "未找到原生 Flash 宿主 BqtjNativeFlashHost.exe。请先运行 tools/Start-DevLauncher.ps1。",
                executablePath);
        }

        for (var attempt = 1; ; attempt++)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            _process = StartHostProcess(executablePath, parentHandle);
            reportDetail?.Invoke(
                $"原生宿主已启动 pid={_process.Id}，等待初始化（第 {attempt} 次）。");
            var detail = new HostStartupDetail();
            if (TryWaitForReady(detail))
            {
                reportDetail?.Invoke($"原生宿主就绪，用时 {stopwatch.ElapsedMilliseconds}ms：{detail.ToSummary()}");
                break;
            }

            StopHostProcess();
            var reason = $"原生 Flash 宿主在 {stopwatch.ElapsedMilliseconds}ms 内没有完成初始化：{detail.ToSummary()}";
            reportDetail?.Invoke(reason);
            if (attempt >= MaximumHostStartAttempts)
            {
                throw new InvalidOperationException($"{reason}请关闭其它游戏窗口后重试，仍未恢复请附上日志反馈。");
            }
        }

        // AppContainer 兼容性探针暂时禁用 Core Audio；正式隔离链必须由普通面板 broker 接管音频。
        if (_audioControlEnabled)
        {
            // 仅在宿主确认可用后启动后台音频校准，避免初始化失败遗留监控任务。
            _audioMute.Attach(_process!.Id);
        }
        foreach (var command in NativeHostStartupCommands.Create(width, height, initialScale))
        {
            SendCommand(command);
        }
        _hostWidth = width;
        _hostHeight = height;
        _pageScale = initialScale;
    }

    private Process StartHostProcess(string executablePath, nint parentHandle)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--parent");
        startInfo.ArgumentList.Add(parentHandle.ToInt64().ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--page");
        startInfo.ArgumentList.Add(_gamePageUri.AbsoluteUri);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动原生 Flash 宿主。");
    }

    /// <summary>按阶段接收宿主输出，直到 ready、进程退出或超时。</summary>
    private bool TryWaitForReady(HostStartupDetail detail)
    {
        var process = _process!;
        while (true)
        {
            var lineTask = process.StandardOutput.ReadLineAsync();
            var timeout = detail.FirstStageReceived
                ? HostInitializationTimeout
                : HostFirstStageTimeout;
            if (!lineTask.Wait(timeout))
            {
                // 读超时后该任务仍占用 stdout 流，这里不再复用进程，交由调用方重启。
                CollectHostStderr(detail);
                return false;
            }

            var line = lineTask.Result;
            if (line is null)
            {
                detail.ExitCode = process.HasExited ? process.ExitCode : null;
                CollectHostStderr(detail);
                return false;
            }

            if (ClassifyHostLine(line) == HostLineKind.Ready)
            {
                return true;
            }

            switch (ClassifyHostLine(line))
            {
                case HostLineKind.Stage:
                    detail.FirstStageReceived = true;
                    detail.Stages.Add(line["stage ".Length..].Trim());
                    break;
                default:
                    // ready 之前的其它输出同样记录，便于判断宿主走到了哪一步。
                    detail.OtherLines.Add(line);
                    break;
            }
        }
    }

    /// <summary>启动期的宿主输出分类：就绪回执、阶段上报或普通输出。</summary>
    internal enum HostLineKind
    {
        Ready,
        Stage,
        Other,
    }

    internal static HostLineKind ClassifyHostLine(string line)
    {
        if (string.Equals(line, "ready", StringComparison.Ordinal))
        {
            return HostLineKind.Ready;
        }

        return line.StartsWith("stage ", StringComparison.Ordinal) && line.Length > "stage ".Length
            ? HostLineKind.Stage
            : HostLineKind.Other;
    }

    private void CollectHostStderr(HostStartupDetail detail)
    {
        try
        {
            if (_process is { HasExited: true })
            {
                var error = _process.StandardError.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(error))
                {
                    detail.Stderr = error.Trim();
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            // 进程已被回收时读取 stderr 会失败，这不影响超时结论。
        }
    }

    private void StopHostProcess()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 进程可能刚好自行退出；重试路径不需要在这里失败。
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    /// <summary>宿主启动过程的现场信息，用于日志和失败提示，不含任何凭据。</summary>
    internal sealed class HostStartupDetail
    {
        public bool FirstStageReceived { get; set; }

        public List<string> Stages { get; } = [];

        public List<string> OtherLines { get; } = [];

        public string? Stderr { get; set; }

        public int? ExitCode { get; set; }

        public string ToSummary()
        {
            var parts = new List<string>();
            parts.Add(Stages.Count == 0 ? "未收到任何启动阶段" : $"阶段={string.Join('>', Stages)}");
            if (OtherLines.Count > 0)
            {
                parts.Add($"输出={string.Join('|', OtherLines)}");
            }

            if (ExitCode is { } exitCode)
            {
                parts.Add($"退出码={exitCode}");
            }

            if (!string.IsNullOrWhiteSpace(Stderr))
            {
                parts.Add($"stderr={Stderr}");
            }

            return string.Join("；", parts);
        }
    }

    /// <summary>仅调整当前原生游戏进程的音频会话，不改变系统或其他账号音量。</summary>
    public void SetMuted(bool isMuted)
    {
        EnsureRunning();
        if (_audioControlEnabled)
        {
            _audioMute.SetMuted(isMuted);
        }
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0 || !CanSendCommand())
        {
            return;
        }

        SendCommand($"resize {width} {height}");
        _hostWidth = width;
        _hostHeight = height;
    }

    /// <summary>刷新游戏页，并等待原生浏览器确认 Refresh 已实际执行。</summary>
    public async Task ReloadAsync()
    {
        await _responseGate.WaitAsync();
        try
        {
            EnsureRunning();
            await _process!.StandardInput.WriteLineAsync("reload");
            await _process.StandardInput.FlushAsync();
            var response = await ReadHostLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));
            if (!string.Equals(response, "reload-ok", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"刷新游戏失败：{response ?? "原生宿主已退出"}");
            }

            /*
             * reload 会销毁并重建 IWebBrowser2，新控件不会可靠继承宿主客户区与光学缩放。
             * 必须在刷新回执之后、画面重新显示之前先恢复物理客户区，再恢复窗口倍率。
             */
            var restoreCommands = NativeHostReloadCommands.Create(
                _hostWidth,
                _hostHeight,
                _pageScale);
            foreach (var command in restoreCommands.Skip(1))
            {
                await _process.StandardInput.WriteLineAsync(command);
            }
            await _process.StandardInput.FlushAsync();
        }
        finally
        {
            _responseGate.Release();
        }
    }

    /// <summary>等待页面或 Flash 就绪；超时后仍显示页面，避免网络故障被加载层永久遮住。</summary>
    public async Task WaitForDisplayReadyAsync(CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(12))
        {
            await _responseGate.WaitAsync(cancellationToken);
            try
            {
                EnsureRunning();
                await _process!.StandardInput.WriteLineAsync("display-ready");
                await _process.StandardInput.FlushAsync(cancellationToken);
                var response = await ReadHostLineAsync(cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                if (response == "display-ready") return;
                if (response != "display-pending") throw new InvalidOperationException("无法确认游戏加载状态。");
            }
            finally { _responseGate.Release(); }
            await Task.Delay(200, cancellationToken);
        }
    }

    /// <summary>在尺寸、缩放和 Flash 初始化完成后，同步显示原生游戏子窗口。</summary>
    public async Task ShowAsync()
    {
        await _responseGate.WaitAsync();
        try
        {
            EnsureRunning();
            await _process!.StandardInput.WriteLineAsync("show");
            await _process.StandardInput.FlushAsync();
            var response = await ReadHostLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));
            if (!string.Equals(response, "show-ok", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"显示游戏画面失败：{response ?? "原生宿主已退出"}");
            }
        }
        finally
        {
            _responseGate.Release();
        }
    }

    public void SetScale(decimal scale)
    {
        EnsureRunning();
        var percentage = decimal.ToInt32(decimal.Round(scale * 100m));
        SendCommand($"scale {percentage.ToString(CultureInfo.InvariantCulture)}");
        _pageScale = scale;
    }

    /// <summary>串行应用倍率，并返回该请求真正执行前的档位。</summary>
    public async Task<SpeedMultiplier> ApplySpeedAsync(SpeedMultiplier multiplier)
    {
        await _responseGate.WaitAsync();
        try
        {
            EnsureRunning();
            var previous = Current;
            if (multiplier == Current)
            {
                return previous;
            }

            await SendSpeedCommandAsync(multiplier);
            Current = multiplier;
            return previous;
        }
        finally
        {
            _responseGate.Release();
        }
    }

    /// <summary>发送单次倍率命令，并以原生宿主回执作为成功边界。</summary>
    private async Task SendSpeedCommandAsync(SpeedMultiplier multiplier)
    {
        await _process!.StandardInput.WriteLineAsync(
            $"speed {multiplier.Value.ToString(CultureInfo.InvariantCulture)}");
        await _process.StandardInput.FlushAsync();
        var response = await ReadHostLineAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));
        if (!string.Equals(response, "speed-ok", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"原生变速组件调用失败：{response ?? "宿主已退出"}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_process is { HasExited: false } process)
        {
            try
            {
                process.StandardInput.WriteLine("exit");
                process.StandardInput.Close();
                if (!process.WaitForExit(2000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
            }
            catch (InvalidOperationException)
            {
                // The child exited between the HasExited check and the shutdown command.
            }
        }

        _process?.Dispose();
        _process = null;
        _audioMute.Dispose();
        _disposed = true;
    }

    private bool CanSendCommand() =>
        !_disposed && _process is { HasExited: false };

    /// <summary>
    /// 读取一行宿主输出，并把每一行交给观察口（用于现场日志）。
    /// </summary>
    private async Task<string?> ReadHostLineAsync(CancellationToken cancellationToken = default)
    {
        var host = _process ?? throw new InvalidOperationException("原生 Flash 宿主尚未启动。");
        var line = await host.StandardOutput.ReadLineAsync(cancellationToken).AsTask();
        if (line is not null)
        {
            LineObserver?.Invoke(line);
        }

        return line;
    }

    private void EnsureRunning()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is null || _process.HasExited)
        {
            var detail = _process is null
                ? "尚未启动"
                : $"已退出（代码 {_process.ExitCode}）";
            throw new InvalidOperationException($"原生 Flash 宿主{detail}。");
        }
    }

    private void SendCommand(string command)
    {
        try
        {
            _process!.StandardInput.WriteLine(command);
            _process.StandardInput.Flush();
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("无法向原生 Flash 宿主发送命令。", exception);
        }
    }

}

/// <summary>生成原生宿主首次显示前的命令，并避免原速档触发无意义的 IE 光学缩放。</summary>
internal static class NativeHostStartupCommands
{
    public static IReadOnlyList<string> Create(int width, int height, decimal scale)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(scale, 0);

        var commands = new List<string>
        {
            $"resize {width.ToString(CultureInfo.InvariantCulture)} {height.ToString(CultureInfo.InvariantCulture)}",
        };
        if (Math.Abs(scale - 1m) > 0.001m)
        {
            var percentage = decimal.ToInt32(decimal.Round(scale * 100m));
            commands.Add($"scale {percentage.ToString(CultureInfo.InvariantCulture)}");
        }

        return commands;
    }
}

/// <summary>
/// 生成刷新浏览器后的显示状态恢复命令；原速依赖新控件默认值，避免无意义的光学缩放。
/// </summary>
internal static class NativeHostReloadCommands
{
    public static IReadOnlyList<string> Create(int width, int height, decimal scale)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(scale, 0);

        var commands = new List<string>
        {
            "reload",
            $"resize {width.ToString(CultureInfo.InvariantCulture)} {height.ToString(CultureInfo.InvariantCulture)}",
        };
        if (Math.Abs(scale - 1m) > 0.001m)
        {
            var percentage = decimal.ToInt32(decimal.Round(scale * 100m));
            commands.Add($"scale {percentage.ToString(CultureInfo.InvariantCulture)}");
        }

        return commands;
    }
}
