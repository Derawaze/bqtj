using System.Diagnostics;
using System.Globalization;
using System.IO;
using BqtjLauncher.Domain;

namespace BqtjLauncher.Runtime.Flash;

internal sealed class NativeFlashHostController : IDisposable
{
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

    /// <summary>只通过已绑定子进程的控制管道传递限长凭据，编码避免换行注入协议。</summary>
    public void ConfigureCredential(BqtjLauncher.Application.AccountCredential? credential)
    {
        if (credential is null) return;
        if (credential.Username.Length > 256 || credential.Password.Length > 1024
            || credential.Username.Contains('\0') || credential.Password.Contains('\0')) return;
        SendCommand("credential " + Convert.ToHexString(System.Text.Encoding.Unicode.GetBytes(credential.Username))
            + ":" + Convert.ToHexString(System.Text.Encoding.Unicode.GetBytes(credential.Password)));
    }

    public void Start(nint parentHandle, int width, int height, decimal initialScale)
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

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动原生 Flash 宿主。");
        var ready = _process.StandardOutput.ReadLineAsync()
            .WaitAsync(TimeSpan.FromSeconds(10))
            .GetAwaiter()
            .GetResult();
        if (!string.Equals(ready, "ready", StringComparison.Ordinal))
        {
            var error = _process.StandardError.ReadToEnd();
            throw new InvalidOperationException(
                $"原生 Flash 宿主初始化失败：{(string.IsNullOrWhiteSpace(error) ? ready : error.Trim())}");
        }

        // AppContainer 兼容性探针暂时禁用 Core Audio；正式隔离链必须由普通面板 broker 接管音频。
        if (_audioControlEnabled)
        {
            // 仅在宿主确认可用后启动后台音频校准，避免初始化失败遗留监控任务。
            _audioMute.Attach(_process.Id);
        }
        foreach (var command in NativeHostStartupCommands.Create(width, height, initialScale))
        {
            SendCommand(command);
        }
        _hostWidth = width;
        _hostHeight = height;
        _pageScale = initialScale;
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
            var response = await _process.StandardOutput.ReadLineAsync()
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
                var response = await _process.StandardOutput.ReadLineAsync(cancellationToken)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
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
            var response = await _process.StandardOutput.ReadLineAsync()
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
        var response = await _process.StandardOutput.ReadLineAsync()
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
