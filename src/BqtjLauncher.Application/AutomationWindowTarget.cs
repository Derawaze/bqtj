using BqtjLauncher.Domain;

namespace BqtjLauncher.Application;

/// <summary>容器提供的本会话Flash目标；不含凭据或账号名称，句柄跨位数用64位整数传输。</summary>
public sealed record AutomationWindowTarget(Guid SessionId, Guid ProfileId, int ContainerProcessId,
    int NativeProcessId, long FlashWindowHandle, int Width, int Height);

/// <summary>专用后台启动入口；容器加载完成时也不得激活窗口。</summary>
public interface IAutomationGameRuntime : IGameRuntime
{
    Task<IGameSession> StartAutomationAsync(GameProfile profile, CancellationToken cancellationToken = default);
}

/// <summary>绑定会话的容器通信能力；目标获取和倍率设置都不通过桌面窗口猜测。</summary>
public interface IAutomationGameSession : IGameSession
{
    Task<AutomationWindowTarget> GetTargetAsync(CancellationToken cancellationToken = default);
    Task<SpeedMultiplier> ApplySpeedAsync(SpeedMultiplier speed, CancellationToken cancellationToken = default);
}
