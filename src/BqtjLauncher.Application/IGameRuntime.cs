using BqtjLauncher.Domain;

namespace BqtjLauncher.Application;

public interface IGameRuntime
{
    Task<IGameSession> StartAsync(GameProfile profile, CancellationToken cancellationToken = default);
}

public interface IGameSession
{
    Guid Id { get; }

    Guid ProfileId { get; }

    Task Completion { get; }

    void Activate();

    Task CloseAsync(CancellationToken cancellationToken = default);
}
