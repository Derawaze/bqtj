using BqtjLauncher.Domain;

namespace BqtjLauncher.Application.Tests;

public sealed class LauncherModuleTests
{
    [Fact]
    public async Task StartAsyncPreventsDuplicateProfileAndActivatesExistingWindow()
    {
        var repository = new InMemoryProfileRepository();
        var runtime = new FakeRuntime();
        await using var launcher = new LauncherModule(repository, runtime);
        var profile = await launcher.CreateProfileAsync("主账号");

        await launcher.StartAsync(profile.Id);

        await Assert.ThrowsAsync<ProfileAlreadyRunningException>(
            () => launcher.StartAsync(profile.Id));
        Assert.Equal(1, runtime.StartCount);
        Assert.Equal(1, runtime.LastSession!.ActivateCount);
    }

    [Fact]
    public async Task StartAsyncEnforcesConcurrentSessionLimit()
    {
        var repository = new InMemoryProfileRepository();
        var runtime = new FakeRuntime();
        await using var launcher = new LauncherModule(
            repository,
            runtime,
            maximumConcurrentSessions: 1);
        var first = await launcher.CreateProfileAsync("账号一");
        var second = await launcher.CreateProfileAsync("账号二");
        await launcher.StartAsync(first.Id);

        await Assert.ThrowsAsync<SessionLimitReachedException>(
            () => launcher.StartAsync(second.Id));
        Assert.Equal(1, runtime.StartCount);
    }

    [Fact]
    public async Task CompletedSessionReleasesProfileForAnotherLaunch()
    {
        var repository = new InMemoryProfileRepository();
        var runtime = new FakeRuntime();
        await using var launcher = new LauncherModule(repository, runtime);
        var profile = await launcher.CreateProfileAsync("账号");
        var completionObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var eventCount = 0;
        launcher.SessionsChanged += (_, _) =>
        {
            if (Interlocked.Increment(ref eventCount) >= 2)
            {
                completionObserved.TrySetResult();
            }
        };

        await launcher.StartAsync(profile.Id);
        runtime.LastSession!.Complete();
        await completionObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await launcher.StartAsync(profile.Id);

        Assert.Equal(2, runtime.StartCount);
    }

    [Fact]
    public async Task DeleteProfileAsyncRejectsRunningProfile()
    {
        var repository = new InMemoryProfileRepository();
        var runtime = new FakeRuntime();
        await using var launcher = new LauncherModule(repository, runtime);
        var profile = await launcher.CreateProfileAsync("账号");
        await launcher.StartAsync(profile.Id);

        await Assert.ThrowsAsync<ProfileInUseException>(
            () => launcher.DeleteProfileAsync(profile.Id));
        Assert.NotNull(await repository.GetAsync(profile.Id));
    }

    [Fact]
    public async Task RestartAsyncClosesHungSessionBeforeStartingReplacement()
    {
        var repository = new InMemoryProfileRepository();
        var runtime = new FakeRuntime();
        await using var launcher = new LauncherModule(repository, runtime);
        var profile = await launcher.CreateProfileAsync("账号");

        await launcher.StartAsync(profile.Id);
        var original = runtime.LastSession!;

        await launcher.RestartAsync(profile.Id);

        Assert.Equal(1, original.CloseCount);
        Assert.Equal(2, runtime.StartCount);
        Assert.NotSame(original, runtime.LastSession);
        Assert.Contains(profile.Id, await launcher.GetActiveProfileIdsAsync());
    }

    [Fact]
    public async Task DisposeDoesNotWaitForStoppedUiDispatcher()
    {
        var runtime = new FakeRuntime();
        var launcher = new LauncherModule(new InMemoryProfileRepository(), runtime);
        var profile = await launcher.CreateProfileAsync("退出回归虚构账号");
        await launcher.StartAsync(profile.Id);
        runtime.LastSession!.CloseDelay = TimeSpan.FromMilliseconds(50);
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    new System.Windows.Threading.DispatcherSynchronizationContext());
                finished.SetResult(launcher.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)));
            }
            catch (Exception exception) { finished.SetException(exception); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(await finished.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AutomationSkipsExistingManualSessionWithoutActivation()
    {
        var runtime = new FakeRuntime();
        await using var launcher = new LauncherModule(new InMemoryProfileRepository(), runtime);
        var profile = await launcher.CreateProfileAsync("虚构手动账号");
        await launcher.StartAsync(profile.Id);

        Assert.Null(await launcher.StartAutomationSessionAsync(profile.Id));
        Assert.Equal(1, runtime.StartCount);
        Assert.Equal(0, runtime.LastSession!.ActivateCount);
        Assert.Equal(0, runtime.LastSession.CloseCount);
    }

    [Fact]
    public async Task ConcurrentAutomationStartsOnlyIssueOneOwnership()
    {
        var runtime = new FakeRuntime();
        await using var launcher = new LauncherModule(new InMemoryProfileRepository(), runtime);
        var profile = await launcher.CreateProfileAsync("虚构并发账号");
        var results = await Task.WhenAll(launcher.StartAutomationSessionAsync(profile.Id),
            launcher.StartAutomationSessionAsync(profile.Id));

        Assert.Single(results, session => session is not null);
        Assert.Equal(1, runtime.StartCount);
        Assert.Equal(1, runtime.AutomationStartCount);
        Assert.Equal(0, runtime.LastSession!.ActivateCount);
    }

    [Fact]
    public async Task OwnedClosePreservesOtherManualSessionAndCannotCloseTwice()
    {
        var runtime = new FakeRuntime();
        await using var launcher = new LauncherModule(new InMemoryProfileRepository(), runtime);
        var manualProfile = await launcher.CreateProfileAsync("虚构保留窗口");
        var automationProfile = await launcher.CreateProfileAsync("虚构脚本窗口");
        await launcher.StartAsync(manualProfile.Id);
        var manual = runtime.LastSession!;
        var owned = (await launcher.StartAutomationSessionAsync(automationProfile.Id))!;
        var automatic = runtime.LastSession!;

        Assert.Equal(automatic.Id, owned.Id);
        Assert.Equal(automationProfile.Id, owned.ProfileId);
        Assert.True(await owned.CloseAsync());
        Assert.True(owned.Completion.IsCompletedSuccessfully);
        Assert.False(await owned.CloseAsync());
        Assert.Equal(1, automatic.CloseCount);
        Assert.Equal(0, manual.CloseCount);
        Assert.Contains(manualProfile.Id, await launcher.GetActiveProfileIdsAsync());
        // 关闭任务返回后须已释放账号，后续存档不能因完成回调尚未调度而被误跳过。
        Assert.DoesNotContain(automationProfile.Id, await launcher.GetActiveProfileIdsAsync());
        Assert.NotNull(await launcher.StartAutomationSessionAsync(automationProfile.Id));
    }

    [Fact]
    public async Task OldOwnershipCannotCloseRestartedAccountWindow()
    {
        var runtime = new FakeRuntime();
        await using var launcher = new LauncherModule(new InMemoryProfileRepository(), runtime);
        var profile = await launcher.CreateProfileAsync("虚构重启账号");
        var old = (await launcher.StartAutomationSessionAsync(profile.Id))!;
        await launcher.RestartAsync(profile.Id);
        var replacement = runtime.LastSession!;

        Assert.False(await old.CloseAsync());
        Assert.Equal(0, replacement.CloseCount);
        Assert.Contains(profile.Id, await launcher.GetActiveProfileIdsAsync());
    }

    [Fact]
    public async Task CanceledAutomationLaunchDoesNotCreateSession()
    {
        var runtime = new FakeRuntime();
        await using var launcher = new LauncherModule(new InMemoryProfileRepository(), runtime);
        var profile = await launcher.CreateProfileAsync("虚构取消启动");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            launcher.StartAutomationSessionAsync(profile.Id, cancellation.Token));
        Assert.Equal(0, runtime.StartCount);
    }

    [Fact]
    public async Task CanceledOwnedClosePreservesWindow()
    {
        var runtime = new FakeRuntime();
        await using var launcher = new LauncherModule(new InMemoryProfileRepository(), runtime);
        var profile = await launcher.CreateProfileAsync("虚构取消关闭");
        var owned = (await launcher.StartAutomationSessionAsync(profile.Id))!;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owned.CloseAsync(cancellation.Token));
        Assert.Equal(0, runtime.LastSession!.CloseCount);
        Assert.False(owned.Completion.IsCompleted);
    }

    private sealed class InMemoryProfileRepository : IGameProfileRepository
    {
        private readonly Dictionary<Guid, GameProfile> _items = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<GameProfile>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameProfile>>([.. _items.Values]);

        public Task<GameProfile?> GetAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.GetValueOrDefault(id));

        public Task UpsertAsync(
            GameProfile profile,
            CancellationToken cancellationToken = default)
        {
            _items[profile.Id] = profile;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _items.Remove(id);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRuntime : IAutomationGameRuntime
    {
        public int AutomationStartCount { get; private set; }

        public Task<IGameSession> StartAutomationAsync(GameProfile profile, CancellationToken cancellationToken = default)
        {
            AutomationStartCount++;
            return StartAsync(profile, cancellationToken);
        }
        public int StartCount { get; private set; }

        public FakeSession? LastSession { get; private set; }

        public Task<GamePageResolution> ResolveGamePageAsync() =>
            Task.FromResult(new GamePageResolution(
                new Uri("https://sbai.4399.com/4399swf/upload_swf/ftp15/linxy/20150324/gun/v3680d.htm"),
                "3680d",
                GamePageResolutionSource.PinnedFallback));

        public Task<IGameSession> StartAsync(
            GameProfile profile,
            CancellationToken cancellationToken = default)
        {
            StartCount++;
            LastSession = new FakeSession(profile.Id);
            return Task.FromResult<IGameSession>(LastSession);
        }
    }

    private sealed class FakeSession(Guid profileId) : IGameSession
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid Id { get; } = Guid.NewGuid();

        public Guid ProfileId { get; } = profileId;

        public Task Completion => _completion.Task;

        public int ActivateCount { get; private set; }

        public int CloseCount { get; private set; }

        public TimeSpan CloseDelay { get; set; }

        public void Activate() => ActivateCount++;

        public async Task CloseAsync(CancellationToken cancellationToken = default)
        {
            CloseCount++;
            if (CloseDelay > TimeSpan.Zero) await Task.Delay(CloseDelay, cancellationToken).ConfigureAwait(false);
            _completion.TrySetResult();
        }

        public void Complete() => _completion.TrySetResult();
    }
}
