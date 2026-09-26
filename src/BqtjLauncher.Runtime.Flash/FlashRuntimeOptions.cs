namespace BqtjLauncher.Runtime.Flash;

public sealed record FlashRuntimeOptions
{
    public required Uri GamePageUri { get; init; }

    public int WindowWidth { get; init; } = 1000;

    public int WindowHeight { get; init; } = 690;

    /// <summary>
    /// 容器启动过程细节（宿主 pid、启动阶段与耗时）的输出口，由组合根接到日志。
    /// 这里保持委托形式，运行库层不依赖具体日志库。
    /// </summary>
    public Action<string>? ReportStartupDetail { get; init; }

    /// <summary>
    /// 运行期诊断（F3 上报、档位互换等）的输出口，同样由组合根接到日志。
    /// 与启动细节分开，便于按需只保留启动信息。
    /// </summary>
    public Action<string>? ReportDiagnostic { get; init; }

    public void Validate()
    {
        if (!GamePageUri.IsAbsoluteUri || GamePageUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Flash 游戏页地址必须是 HTTP(S) 绝对地址。", nameof(GamePageUri));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(WindowWidth, 800);
        ArgumentOutOfRangeException.ThrowIfLessThan(WindowHeight, 600);

    }
}
