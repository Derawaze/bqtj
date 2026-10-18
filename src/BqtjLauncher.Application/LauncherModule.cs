using BqtjLauncher.Domain;

namespace BqtjLauncher.Application;

/// <summary>管理账号及独立游戏会话；脚本可按账号接管本面板持有的精确会话。</summary>
public sealed class LauncherModule : IAsyncDisposable
{
    private readonly IGameProfileRepository _profiles;
    private readonly IGameRuntime _runtime;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, IGameSession> _sessionsByProfile = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, AutomationSessionMemory> _automationMemory = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _automationOwners = new();

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

    public async Task<Guid> StartAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var session = await StartSessionAsync(profileId, skipExisting: false, cancellationToken);
        return session!.Id;
    }

    /// <summary>脚本只取得本次新建会话；已有手动/自动会话原子跳过，不激活、不接管。</summary>
    public async Task<AutomationGameSession?> StartAutomationSessionAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var session = await StartSessionAsync(profileId, skipExisting: true, cancellationToken);
        return session is null ? null : new AutomationGameSession(session,
            token => CloseAutomationSessionAsync(profileId, session, token));
    }

    /// <summary>原子取得空闲控制权；普通窗口和上次脚本保留的窗口复用，同一窗口只允许一个控制者。</summary>
    public async Task<AutomationGameSession?> AcquireAutomationSessionAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existed = _sessionsByProfile.TryGetValue(profileId, out var session);
            if (!existed)
            {
                var profile = await RequireProfileAsync(profileId, cancellationToken);
                if (_runtime is not IAutomationGameRuntime runtime) throw new NotSupportedException("当前运行库未提供后台自动化能力。");
                session = await runtime.StartAutomationAsync(profile, cancellationToken);
                _sessionsByProfile.Add(profileId, session);
                await _profiles.UpsertAsync(profile.MarkLaunched(_clock()), cancellationToken);
                _ = ObserveCompletionAsync(profileId, session);
                SessionsChanged?.Invoke(this, EventArgs.Empty);
            }
            if (!_automationOwners.TryAdd(session!.Id, 0)) return null;
            var memory = _automationMemory.GetOrAdd(session.Id, _ => new());
            return new AutomationGameSession(session, token => CloseAutomationSessionAsync(profileId, session, token),
                memory, () => _automationOwners.TryRemove(session.Id, out _), existed);
        }
        finally { _gate.Release(); }
    }

    /// <summary>共享启动互斥且不限制账号数量，保持面板重复启动行为与脚本跳过行为各自明确。</summary>
    private async Task<IGameSession?> StartSessionAsync(
        Guid profileId, bool skipExisting, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_sessionsByProfile.TryGetValue(profileId, out var existing))
            {
                if (skipExisting) return null;
                existing.Activate();
                throw new ProfileAlreadyRunningException(profileId);
            }

            var profile = await RequireProfileAsync(profileId, cancellationToken);
            if (skipExisting && _runtime is not IAutomationGameRuntime)
                throw new NotSupportedException("当前运行库未提供后台自动化能力。");
            var session = skipExisting && _runtime is IAutomationGameRuntime automationRuntime
                ? await automationRuntime.StartAutomationAsync(profile, cancellationToken)
                : await _runtime.StartAsync(profile, cancellationToken);
            _sessionsByProfile.Add(profileId, session);

            var launched = profile.MarkLaunched(_clock());
            await _profiles.UpsertAsync(launched, cancellationToken);

            _ = ObserveCompletionAsync(profileId, session);
            SessionsChanged?.Invoke(this, EventArgs.Empty);
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>关闭前核对原会话引用；释放锁后仍操作原对象，避免并发重启误关替代会话。</summary>
    private async Task<bool> CloseAutomationSessionAsync(
        Guid profileId, IGameSession ownedSession, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sessionsByProfile.TryGetValue(profileId, out var current)
                || !ReferenceEquals(current, ownedSession) || ownedSession.Completion.IsCompleted)
                return false;
        }
        finally { _gate.Release(); }

        cancellationToken.ThrowIfCancellationRequested();
        await ownedSession.CloseAsync(cancellationToken).ConfigureAwait(false);
        await ownedSession.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        // 不依赖异步完成回调先得到调度：返回时账号必须已可启动下一存档。
        await ReleaseSessionAsync(profileId, ownedSession).ConfigureAwait(false);
        return true;
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
            await ReleaseSessionAsync(profileId, session).ConfigureAwait(false);
        }
    }

    /// <summary>完成回调与显式关闭共用引用校验，只释放原会话并通知一次，不移除替代窗口。</summary>
    private async Task ReleaseSessionAsync(Guid profileId, IGameSession session)
    {
        var removed = false;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_sessionsByProfile.TryGetValue(profileId, out var current) && ReferenceEquals(current, session))
            {
                removed = _sessionsByProfile.Remove(profileId);
                _automationMemory.TryRemove(session.Id, out _);
                _automationOwners.TryRemove(session.Id, out _);
            }
        }
        finally { _gate.Release(); }

        if (removed) SessionsChanged?.Invoke(this, EventArgs.Empty);
    }
}
