using BqtjLauncher.Domain;

namespace BqtjLauncher.Application;

/// <summary>仅由启动器签发的新建会话凭证；不提供激活窗口或按账号重新查找窗口的入口。</summary>
public sealed class AutomationGameSession
{
    private readonly Func<CancellationToken, Task<bool>> _close;
    private readonly IAutomationGameSession? _control;

    internal AutomationGameSession(IGameSession session, Func<CancellationToken, Task<bool>> close)
    {
        Id = session.Id;
        ProfileId = session.ProfileId;
        Completion = session.Completion;
        _close = close;
        _control = session as IAutomationGameSession;
    }

    public Guid Id { get; }
    public Guid ProfileId { get; }
    public Task Completion { get; }

    /// <summary>读取自身容器的目标；已退出的凭证不再允许控制，调用方仍需逐帧校验句柄。</summary>
    public Task<AutomationWindowTarget> GetTargetAsync(CancellationToken cancellationToken = default)
        => RequireControl().GetTargetAsync(cancellationToken);

    /// <summary>临时倍率不写入用户偏好；返回原倍率，供停止或失败时恢复。</summary>
    public Task<SpeedMultiplier> ApplySpeedAsync(SpeedMultiplier speed, CancellationToken cancellationToken = default)
        => RequireControl().ApplySpeedAsync(speed, cancellationToken);

    private IAutomationGameSession RequireControl()
    {
        if (Completion.IsCompleted) throw new InvalidOperationException("脚本会话已结束。");
        return _control ?? throw new NotSupportedException("当前运行库没有自动化容器通信能力。");
    }

    /// <summary>只关闭签发时绑定的会话；已退出或被替换返回false，不触碰同账号的新窗口。</summary>
    public Task<bool> CloseAsync(CancellationToken cancellationToken = default) => _close(cancellationToken);
}
