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

        public void Activate() => ActivateCount++;

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            CloseCount++;
            _completion.TrySetResult();
            return Task.CompletedTask;
        }

        public void Complete() => _completion.TrySetResult();
    }
}
