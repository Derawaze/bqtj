using BqtjLauncher.Domain;

namespace BqtjLauncher.Application;

public sealed class LauncherModule : IAsyncDisposable
{
    private readonly IGameProfileRepository _profiles;
    private readonly IGameRuntime _runtime;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, IGameSession> _sessionsByProfile = [];

    public LauncherModule(
        IGameProfileRepository profiles,
        IGameRuntime runtime,
        int maximumConcurrentSessions = 4,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrentSessions, 1);
        _profiles = profiles;
        _runtime = runtime;
        MaximumConcurrentSessions = maximumConcurrentSessions;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public event EventHandler? SessionsChanged;

    public int MaximumConcurrentSessions { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default) =>
        await _profiles.InitializeAsync(cancellationToken);

    public Task<IReadOnlyList<GameProfile>> ListProfilesAsync(
        CancellationToken cancellationToken = default) =>
        _profiles.ListAsync(cancellationToken);

    public async Task<GameProfile> CreateProfileAsync(
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var profile = GameProfile.Create(displayName, _clock());
        await _profiles.UpsertAsync(profile, cancellationToken);
        return profile;
    }

    public async Task<GameProfile> RenameProfileAsync(
        Guid profileId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var profile = await RequireProfileAsync(profileId, cancellationToken);
        var renamed = profile.Rename(displayName);
        await _profiles.UpsertAsync(renamed, cancellationToken);
        return renamed;
    }

    public async Task DeleteProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_sessionsByProfile.ContainsKey(profileId))
            {
                throw new ProfileInUseException(profileId);
            }

            await _profiles.DeleteAsync(profileId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Guid> StartAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_sessionsByProfile.TryGetValue(profileId, out var existing))
            {
                existing.Activate();
                throw new ProfileAlreadyRunningException(profileId);
            }

            if (_sessionsByProfile.Count >= MaximumConcurrentSessions)
            {
                throw new SessionLimitReachedException(MaximumConcurrentSessions);
            }

            var profile = await RequireProfileAsync(profileId, cancellationToken);
            var session = await _runtime.StartAsync(profile, cancellationToken);
            _sessionsByProfile.Add(profileId, session);

            var launched = profile.MarkLaunched(_clock());
            await _profiles.UpsertAsync(launched, cancellationToken);

            _ = ObserveCompletionAsync(profileId, session);
            SessionsChanged?.Invoke(this, EventArgs.Empty);
            return session.Id;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlySet<Guid>> GetActiveProfileIdsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _sessionsByProfile.Keys.ToHashSet();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        IGameSession session;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            session = _sessionsByProfile.GetValueOrDefault(profileId)
                ?? throw new InvalidOperationException("该账号当前没有正在运行的游戏容器。");
        }
        finally
        {
            _gate.Release();
        }

        await session.CloseAsync(cancellationToken);
        await session.Completion.WaitAsync(cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_sessionsByProfile.TryGetValue(profileId, out var current)
                && current.Id == session.Id)
            {
                _sessionsByProfile.Remove(profileId);
            }
        }
        finally
        {
            _gate.Release();
        }

        await StartAsync(profileId, cancellationToken);
    }

    public async Task CloseAllAsync(CancellationToken cancellationToken = default)
    {
        IGameSession[] sessions;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            sessions = [.. _sessionsByProfile.Values];
        }
        finally
        {
            _gate.Release();
        }

        foreach (var session in sessions)
        {
            await session.CloseAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAllAsync();
    }

    private async Task<GameProfile> RequireProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken) =>
        await _profiles.GetAsync(profileId, cancellationToken)
        ?? throw new ProfileNotFoundException(profileId);

    private async Task ObserveCompletionAsync(Guid profileId, IGameSession session)
    {
        try
        {
            await session.Completion;
        }
        finally
        {
            await _gate.WaitAsync();
            try
            {
                if (_sessionsByProfile.TryGetValue(profileId, out var current)
                    && current.Id == session.Id)
                {
                    _sessionsByProfile.Remove(profileId);
                }
            }
            finally
            {
                _gate.Release();
            }

            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
