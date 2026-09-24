using BqtjLauncher.Domain;

namespace BqtjLauncher.Application;

public interface IGameRuntime
{
    Task<IGameSession> StartAsync(GameProfile profile, CancellationToken cancellationToken = default);

    /// <summary>
    /// 解析平台当前发布的游戏入口，供面板展示和启动前确认。
    /// 实现应在网络不可用时回退到可用入口，而不是让面板加载失败。
    /// </summary>
    Task<GamePageResolution> ResolveGamePageAsync();
}

public interface IGameSession
{
    Guid Id { get; }

    Guid ProfileId { get; }

    Task Completion { get; }

    void Activate();

    Task CloseAsync(CancellationToken cancellationToken = default);
}
