using BqtjLauncher.Domain;

namespace BqtjLauncher.Application;

/// <summary>管理本地账号及独立游戏会话，按账号去重并负责重启、关闭；多开数量由用户按电脑资源选择。</summary>
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
        Func<DateTimeOffset>? clock = null)
    {
        _profiles = profiles;
        _runtime = runtime;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public event EventHandler? SessionsChanged;

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

    /// <summary>串行登记启动结果以避免账号重复；不同账号不设固定数量上限。</summary>
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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            await session.CloseAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // 释放链各层都不捕获界面上下文，避免应用退出时同步等待形成死锁。
        await CloseAllAsync().ConfigureAwait(false);
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
