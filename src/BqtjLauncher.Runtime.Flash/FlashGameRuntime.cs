using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BqtjLauncher.Application;
using BqtjLauncher.Domain;

namespace BqtjLauncher.Runtime.Flash;

public sealed class FlashGameRuntime : IAutomationGameRuntime
{
    private readonly FlashRuntimeOptions _options;
    private readonly IGamePageSource? _gamePages;
    private readonly Uri _sourcePageUri;
    private readonly Uri _pinnedGamePageUri;
    private readonly object _resolutionGate = new();
    private Task<GamePageResolution>? _resolution;

    public FlashGameRuntime(FlashRuntimeOptions options, IGamePageSource? gamePages = null)
    {
        options.Validate();
        _options = options;
        _gamePages = gamePages;
        _sourcePageUri = GamePageDefaults.SourcePageUri;
        _pinnedGamePageUri = GamePageDefaults.PinnedGamePageUri;
    }

    public Task<GamePageResolution> GamePage
    {
        get
        {
            lock (_resolutionGate)
            {
                return _resolution ??= ResolveGamePageAsync();
            }
        }
    }

    /// <summary>
    /// 重新解析平台当前发布的版本。面板启动时调用一次用于展示状态；
    /// 结果只缓存在内存，不跨启动复用，因此平台发布新版本后重启启动器即可生效。
    /// </summary>
    public Task<GamePageResolution> ResolveGamePageAsync()
    {
        var resolution = ResolveGamePageCoreAsync();
        lock (_resolutionGate)
        {
            _resolution = resolution;
        }

        return resolution;
    }

    private async Task<GamePageResolution> ResolveGamePageCoreAsync()
    {
        if (_gamePages is null)
        {
            return new GamePageResolution(
                _options.GamePageUri,
                GamePageHtml.ReadVersionLabel(_options.GamePageUri),
                GamePageResolutionSource.PinnedFallback,
                "未配置版本解析器。");
        }

        return await _gamePages.ResolveAsync(_sourcePageUri, _pinnedGamePageUri);
    }

    public async Task<IGameSession> StartAsync(
        GameProfile profile,
        CancellationToken cancellationToken = default)
        => await StartCoreAsync(profile, null, cancellationToken);

    /// <summary>生成管道身份并传入自身容器，加载完成后也不激活游戏窗口。</summary>
    public Task<IGameSession> StartAutomationAsync(GameProfile profile, CancellationToken cancellationToken = default)
        => StartCoreAsync(profile, Guid.NewGuid(), cancellationToken);

    private async Task<IGameSession> StartCoreAsync(GameProfile profile, Guid? automationSessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runtime = FlashRuntimeDetector.Detect32Bit();
        if (!runtime.IsAvailable)
        {
            throw new InvalidOperationException(runtime.Message);
        }

        // 每次启动都重新解析并采用当次结果，避免长期运行的面板沿用平台已下线的旧版本。
        var gamePage = await ResolveGamePageAsync().WaitAsync(cancellationToken);
        var startInfo = CreateHostStartInfo(profile, gamePage.GamePageUri, automationSessionId);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动独立游戏容器进程。");
        return new FlashGameProcessSession(profile.Id, process, automationSessionId);
    }

    private static ProcessStartInfo CreateHostStartInfo(GameProfile profile, Uri gamePageUri, Guid? automationSessionId)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定启动器进程路径。");
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        if (Path.GetFileName(processPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            // dotnet.exe 开发入口使用磁盘 DLL；正式单文件入口直接重用 ProcessPath。
            var assemblyName = Assembly.GetEntryAssembly()?.GetName().Name
                ?? throw new InvalidOperationException("无法确定启动器程序集名称。");
            var entryAssembly = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
            startInfo.ArgumentList.Add(entryAssembly);
        }

        startInfo.ArgumentList.Add(FlashHostLaunchRequest.ModeArgument);
        startInfo.ArgumentList.Add("--account-id");
        startInfo.ArgumentList.Add(profile.Id.ToString("D"));
        startInfo.ArgumentList.Add("--account-name");
        startInfo.ArgumentList.Add(profile.DisplayName);
        startInfo.ArgumentList.Add("--game-page");
        startInfo.ArgumentList.Add(gamePageUri.AbsoluteUri);
        startInfo.ArgumentList.Add("--panel-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        if (automationSessionId.HasValue)
        {
            startInfo.ArgumentList.Add("--automation-session");
            startInfo.ArgumentList.Add(automationSessionId.Value.ToString("D"));
        }
        return startInfo;
    }
}

internal sealed class FlashGameProcessSession : IAutomationGameSession
{
    private readonly Process _process;
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly bool _automationEnabled;

    public FlashGameProcessSession(Guid profileId, Process process, Guid? automationSessionId = null)
    {
        Id = automationSessionId ?? Guid.NewGuid();
        _automationEnabled = automationSessionId.HasValue;
        ProfileId = profileId;
        _process = process;
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => _completion.TrySetResult();
        if (_process.HasExited)
        {
            _completion.TrySetResult();
        }
    }

    public Guid Id { get; }

    public Guid ProfileId { get; }

    public Task Completion => _completion.Task;

    /// <summary>每次重新查询容器，校验回复身份及容器PID；不缓存可能失效的Flash句柄。</summary>
    public async Task<AutomationWindowTarget> GetTargetAsync(CancellationToken cancellationToken = default)
    {
        RequireAutomation();
        var response = await AutomationSessionPipe.SendAsync(_process.Id, new(Id, "target"), cancellationToken).ConfigureAwait(false);
        var target = response.Target ?? throw new InvalidOperationException("容器没有返回自动化目标。");
        if (target.SessionId != Id || target.ProfileId != ProfileId || target.ContainerProcessId != _process.Id)
            throw new InvalidOperationException("自动化目标与本会话不符。");
        return target;
    }

    /// <summary>只请求自身容器调整运行时倍率并返回原值，不更新本地运行偏好。</summary>
    public async Task<SpeedMultiplier> ApplySpeedAsync(SpeedMultiplier speed, CancellationToken cancellationToken = default)
    {
        RequireAutomation();
        var response = await AutomationSessionPipe.SendAsync(_process.Id, new(Id, "speed", speed.Value), cancellationToken).ConfigureAwait(false);
        return SpeedMultiplier.Create(response.PreviousSpeed ?? throw new InvalidOperationException("容器没有返回原倍率。"));
    }

    private void RequireAutomation()
    {
        if (!_automationEnabled || _process.HasExited) throw new InvalidOperationException("此会话未开放自动化或已退出。");
    }

    public void Activate()
    {
        if (_process.HasExited)
        {
            return;
        }

        _process.Refresh();
        var handle = _process.MainWindowHandle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _ = ShowWindowAsync(handle, 9);
        _ = SetForegroundWindow(handle);
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_process.HasExited)
        {
            return;
        }

        _ = _process.CloseMainWindow();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            // 面板 OnExit 会同步等待释放；退出续体不能依赖正在关闭的 WPF 消息循环。
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);
}
