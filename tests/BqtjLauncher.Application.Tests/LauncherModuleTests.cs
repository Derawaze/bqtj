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

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public async Task StartAsyncAllowsMultipleProfilesWithoutFixedLimit(int accountCount)
    {
        var repository = new InMemoryProfileRepository();
        var runtime = new FakeRuntime();
        await using var launcher = new LauncherModule(repository, runtime);
        var profiles = new List<GameProfile>();
        for (var index = 0; index < accountCount; index++)
        {
            profiles.Add(await launcher.CreateProfileAsync($"多开虚构账号{index}"));
        }

        // 超过原四账号边界后，仍验证身份去重、重启和整批关闭，避免仅修改提示文案。
        await Task.WhenAll(profiles.Select(profile => launcher.StartAsync(profile.Id)));
        Assert.Equal(accountCount, runtime.StartCount);
        Assert.Equal(profiles.Select(profile => profile.Id).Order(),
            (await launcher.GetActiveProfileIdsAsync()).Order());

        var firstSession = runtime.Sessions[0];
        await Assert.ThrowsAsync<ProfileAlreadyRunningException>(() => launcher.StartAsync(profiles[0].Id));
        Assert.Equal(1, firstSession.ActivateCount);
        Assert.Equal(accountCount, runtime.StartCount);

        await launcher.RestartAsync(profiles[0].Id);
        Assert.Equal(1, firstSession.CloseCount);
        Assert.Equal(accountCount + 1, runtime.StartCount);
        Assert.Equal(accountCount, (await launcher.GetActiveProfileIdsAsync()).Count);

        await launcher.CloseAllAsync();
        Assert.All(runtime.Sessions, session => Assert.Equal(1, session.CloseCount));
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

    private sealed class FakeRuntime : IGameRuntime
    {
        public int StartCount { get; private set; }

        public FakeSession? LastSession { get; private set; }

        public List<FakeSession> Sessions { get; } = [];

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
            Sessions.Add(LastSession);
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
