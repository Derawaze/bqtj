using BqtjLauncher.Domain;

namespace BqtjLauncher.Application;

/// <summary>启动器签发的精确会话凭证；接管仍绑定原窗口，不能按账号误控替代窗口。</summary>
public sealed class AutomationGameSession
{
    private readonly Func<CancellationToken, Task<bool>> _close;
    private readonly IAutomationGameSession? _control;
    private readonly AutomationSessionMemory _memory;
    private readonly Action? _release;
    private int _released;

    internal AutomationGameSession(IGameSession session, Func<CancellationToken, Task<bool>> close,
        AutomationSessionMemory? memory = null, Action? release = null, bool wasExisting = false)
    {
        Id = session.Id;
        ProfileId = session.ProfileId;
        Completion = session.Completion;
        _close = close;
        _control = session as IAutomationGameSession;
        _memory = memory ?? new();
        _release = release;
        WasExisting = wasExisting;
    }

    public Guid Id { get; }
    public Guid ProfileId { get; }
    public Task Completion { get; }
    public bool WasExisting { get; }
    // 同一游戏会话跨方案保留已知存档；0表示接管了无法确定档号的当前游戏。
    public int? CurrentSave { get => _memory.SaveNumber; set => _memory.SaveNumber = value; }
    public bool SafeToExit { get => _memory.SafeToExit; set => _memory.SafeToExit = value; }

    public Task AcquireWindowLockAsync(CancellationToken cancellationToken = default)
        => RequireControl().AcquireWindowLockAsync(cancellationToken);

    /// <summary>读取自身容器的目标；已退出的凭证不再允许控制，调用方仍需逐帧校验句柄。</summary>
    public Task<AutomationWindowTarget> GetTargetAsync(CancellationToken cancellationToken = default)
        => RequireControl().GetTargetAsync(cancellationToken);

    /// <summary>临时倍率不写入用户偏好；返回原倍率，供停止或失败时恢复。</summary>
    public Task<SpeedMultiplier> ApplySpeedAsync(SpeedMultiplier speed, CancellationToken cancellationToken = default)
        => RequireControl().ApplySpeedAsync(speed, cancellationToken);

    /// <summary>仅解除本次脚本窗口的尺寸锁定；不写共享窗口偏好。</summary>
    public async Task ReleaseWindowLockAsync(CancellationToken cancellationToken = default)
    {
        try { if (!Completion.IsCompleted) await RequireControl().ReleaseWindowLockAsync(cancellationToken).ConfigureAwait(false); }
        finally { if (Interlocked.Exchange(ref _released, 1) == 0) _release?.Invoke(); }
    }

    private IAutomationGameSession RequireControl()
    {
        if (Completion.IsCompleted) throw new InvalidOperationException("脚本会话已结束。");
        return _control ?? throw new NotSupportedException("当前运行库没有自动化容器通信能力。");
    }

    /// <summary>只关闭签发时绑定的会话；已退出或被替换返回false，不触碰同账号的新窗口。</summary>
    public Task<bool> CloseAsync(CancellationToken cancellationToken = default) => _close(cancellationToken);
}

/// <summary>只跟随游戏会话生存的执行记忆，不写入用户配置。</summary>
internal sealed class AutomationSessionMemory
{
    public int? SaveNumber { get; set; }
    public bool SafeToExit { get; set; }
}
