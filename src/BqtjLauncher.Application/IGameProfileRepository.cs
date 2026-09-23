using BqtjLauncher.Domain;

namespace BqtjLauncher.Application;

public interface IGameProfileRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GameProfile>> ListAsync(CancellationToken cancellationToken = default);

    Task<GameProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task UpsertAsync(GameProfile profile, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
