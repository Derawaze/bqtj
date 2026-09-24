using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BqtjLauncher.Application;
using BqtjLauncher.Domain;

namespace BqtjLauncher.Runtime.Flash;

public sealed class FlashGameRuntime : IGameRuntime
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
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runtime = FlashRuntimeDetector.Detect32Bit();
        if (!runtime.IsAvailable)
        {
            throw new InvalidOperationException(runtime.Message);
        }

        // 每次启动都重新解析并采用当次结果，避免长期运行的面板沿用平台已下线的旧版本。
        var gamePage = await ResolveGamePageAsync().WaitAsync(cancellationToken);
        var startInfo = CreateHostStartInfo(profile, gamePage.GamePageUri);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动独立游戏容器进程。");
        return new FlashGameProcessSession(profile.Id, process);
    }

    private static ProcessStartInfo CreateHostStartInfo(GameProfile profile, Uri gamePageUri)
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
        return startInfo;
    }
}

internal sealed class FlashGameProcessSession : IGameSession
{
    private readonly Process _process;
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FlashGameProcessSession(Guid profileId, Process process)
    {
        ProfileId = profileId;
        _process = process;
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => _completion.TrySetResult();
        if (_process.HasExited)
        {
            _completion.TrySetResult();
        }
    }

    public Guid Id { get; } = Guid.NewGuid();

    public Guid ProfileId { get; }

    public Task Completion => _completion.Task;

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
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(cancellationToken);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);
}
