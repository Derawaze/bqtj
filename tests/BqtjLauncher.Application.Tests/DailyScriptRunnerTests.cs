using System.Runtime.CompilerServices;
using BqtjLauncher.Domain;

namespace BqtjLauncher.Application.Tests;

/// <summary>虚构视觉组件覆盖完整外层生命周期，防止旧完成、混会话与取消触发关闭。</summary>
public sealed class DailyScriptRunnerTests
{
    [Fact]
    public async Task StableSaveClosesOwnedSessionDespiteIntermediateFailures()
    {
        var runtime = new Runtime();
        await using var launcher = new LauncherModule(new Repository(), runtime);
        var profile = await launcher.CreateProfileAsync("虚构日常账号");
        var clock = new Clock();
        var executor = new Executor(clock, [(0, false, false), (1, false, true),
            (22, true, false), (22, true, false), (22, true, false), (22, true, false)]);

        var result = await new DailyScriptRunner(launcher, executor, clock).RunAsync(profile.Id, 8, SpeedMultiplier.Create(20));

        Assert.Equal(DailyScriptRunStatus.Completed, result.Status);
        Assert.True(result.HasFailedItems);
        Assert.Equal(8, executor.SaveNumber);
        Assert.Equal(1, runtime.Last!.CloseCount);
        Assert.Equal(new decimal[] { 1, 20 }, runtime.Last.Speeds);
        Assert.DoesNotContain(profile.Id, await launcher.GetActiveProfileIdsAsync());
    }

    [Fact]
    public async Task ExistingManualWindowIsSkippedWithoutExecutingOrActivating()
    {
        var runtime = new Runtime();
        await using var launcher = new LauncherModule(new Repository(), runtime);
        var profile = await launcher.CreateProfileAsync("虚构已有窗口");
        await launcher.StartAsync(profile.Id);
        var clock = new Clock();
        var executor = new Executor(clock, []);
        Assert.Equal(DailyScriptRunStatus.Skipped,
            (await new DailyScriptRunner(launcher, executor, clock).RunAsync(profile.Id, 1)).Status);
        Assert.Equal(0, executor.Calls);
        Assert.Equal(0, runtime.Last!.ActivateCount);
        Assert.Equal(0, runtime.Last.CloseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldCompleteOrWrongRunCannotCloseAndRestoresSpeed(bool wrongRun)
    {
        var runtime = new Runtime();
        await using var launcher = new LauncherModule(new Repository(), runtime);
        var profile = await launcher.CreateProfileAsync("虚构旧完成画面");
        var clock = new Clock();
        var executor = new Executor(clock, [(22, true, false), (22, true, false), (22, true, false), (22, true, false)])
        { WrongRun = wrongRun };
        var result = await new DailyScriptRunner(launcher, executor, clock).RunAsync(profile.Id, 1, SpeedMultiplier.Create(20));
        Assert.Equal(DailyScriptRunStatus.Paused, result.Status);
        Assert.Equal(0, runtime.Last!.CloseCount);
        Assert.Equal(new decimal[] { 1, 10 }, runtime.Last.Speeds);
    }

    [Fact]
    public async Task CancelledExecutionPreservesWindowAndRestoresOriginalSpeed()
    {
        var runtime = new Runtime();
        await using var launcher = new LauncherModule(new Repository(), runtime);
        var profile = await launcher.CreateProfileAsync("虚构取消任务");
        var clock = new Clock();
        using var cancellation = new CancellationTokenSource();
        var executor = new Executor(clock, [(0, false, false)]) { BeforeYield = cancellation.Cancel };
        var result = await new DailyScriptRunner(launcher, executor, clock).RunAsync(profile.Id, 1, cancellation.Token);
        Assert.Equal(DailyScriptRunStatus.Cancelled, result.Status);
        Assert.Equal(0, runtime.Last!.CloseCount);
        Assert.Equal(new decimal[] { 1, 10 }, runtime.Last.Speeds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task InvalidSaveRejectedBeforeStartingAccount(int saveNumber)
    {
        var runtime = new Runtime();
        await using var launcher = new LauncherModule(new Repository(), runtime);
        var clock = new Clock();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new DailyScriptRunner(launcher, new Executor(clock, []), clock).RunAsync(Guid.NewGuid(), saveNumber));
        Assert.Null(runtime.Last);
    }

    [Fact]
    public async Task UnexpectedWindowExitStopsEvenIfFrameArrivesAtSameTime()
    {
        var runtime = new Runtime();
        await using var launcher = new LauncherModule(new Repository(), runtime);
        var profile = await launcher.CreateProfileAsync("虚构退出竞态");
        var clock = new Clock();
        var executor = new Executor(clock, [(0, false, false), (1, false, false)])
        { BeforeYield = () => runtime.Last!.CloseAsync().GetAwaiter().GetResult() };
        var result = await new DailyScriptRunner(launcher, executor, clock).RunAsync(profile.Id, 1);
        Assert.Equal(DailyScriptRunStatus.Paused, result.Status);
        Assert.Equal(1, runtime.Last!.CloseCount);
        Assert.Equal(new decimal[] { 1 }, runtime.Last.Speeds);
    }

    [Fact]
    public async Task FailedSpeedRestoreIsReportedInsteadOfSilentlyIgnored()
    {
        var runtime = new Runtime { FailRestore = true };
        await using var launcher = new LauncherModule(new Repository(), runtime);
        var profile = await launcher.CreateProfileAsync("虚构恢复失败");
        var clock = new Clock();
        var result = await new DailyScriptRunner(launcher, new Executor(clock, []), clock).RunAsync(profile.Id, 1);
        Assert.Equal(DailyScriptRunStatus.Paused, result.Status);
        Assert.True(result.NeedsManualSpeedRestore);
        Assert.Equal(0, runtime.Last!.CloseCount);
    }

    [Fact]
    public async Task WorkerFailureKeepsSpecificStageAndRestoresSpeedWithoutClosing()
    {
        var runtime = new Runtime();
        await using var launcher = new LauncherModule(new Repository(), runtime);
        var profile = await launcher.CreateProfileAsync("虚构诊断账号");
        var clock = new Clock();
        var executor = new Executor(clock, [(0, false, false)])
        {
            BeforeYield = () => throw new DailyScriptExecutionException("绑定游戏窗口", DailyScriptFailureCode.DesktopUnavailable),
        };
        var result = await new DailyScriptRunner(launcher, executor, clock).RunAsync(profile.Id, 5);
        Assert.Equal(DailyScriptRunStatus.Paused, result.Status);
        Assert.Contains("绑定游戏窗口", result.Detail);
        Assert.Contains("桌面不可访问", result.Detail);
        Assert.Equal(0, runtime.Last!.CloseCount);
        Assert.Equal(new decimal[] { 1, 10 }, runtime.Last.Speeds);
    }

    private sealed class Clock : TimeProvider
    {
        private long _timestamp = 1000;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        public void Advance() => _timestamp += 1000;
    }

    private sealed class Executor(Clock clock, (int Done, bool Saved, bool Failed)[] observations) : IDailyScriptExecutor
    {
        public int Calls { get; private set; }
        public int SaveNumber { get; private set; }
        public bool WrongRun { get; init; }
        public Action? BeforeYield { get; init; }
        public async IAsyncEnumerable<DailyScriptFrame> ExecuteAsync(AutomationWindowTarget target, int saveNumber,
            Guid runId, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            SaveNumber = saveNumber;
            foreach (var item in observations)
            {
                BeforeYield?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                clock.Advance();
                yield return new(target.SessionId, WrongRun ? Guid.NewGuid() : runId, clock.GetTimestamp(),
                    new(item.Done, 22), item.Saved, item.Failed);
                await Task.CompletedTask;
            }
        }
    }

    private sealed class Runtime : IAutomationGameRuntime
    {
        public bool FailRestore { get; init; }
        public Session? Last { get; private set; }
        public Task<IGameSession> StartAsync(GameProfile profile, CancellationToken cancellationToken = default)
            => Task.FromResult<IGameSession>(Last = new(profile.Id) { FailRestore = FailRestore });
        public Task<IGameSession> StartAutomationAsync(GameProfile profile, CancellationToken cancellationToken = default)
            => StartAsync(profile, cancellationToken);
        public Task<GamePageResolution> ResolveGamePageAsync() => throw new NotSupportedException();
    }

    private sealed class Session(Guid profileId) : IAutomationGameSession
    {
        public bool FailRestore { get; init; }
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private SpeedMultiplier _speed = SpeedMultiplier.Create(10);
        public Guid Id { get; } = Guid.NewGuid();
        public Guid ProfileId => profileId;
        public Task Completion => _completion.Task;
        public int CloseCount { get; private set; }
        public int ActivateCount { get; private set; }
        public List<decimal> Speeds { get; } = [];
        public void Activate() => ActivateCount++;
        public Task CloseAsync(CancellationToken cancellationToken = default)
        { CloseCount++; _completion.TrySetResult(); return Task.CompletedTask; }
        public Task<AutomationWindowTarget> GetTargetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AutomationWindowTarget(Id, ProfileId, 123, 456, 789, 950, 600));
        public Task<SpeedMultiplier> ApplySpeedAsync(SpeedMultiplier speed, CancellationToken cancellationToken = default)
        {
            if (FailRestore && Speeds.Count > 0) throw new InvalidOperationException("虚构恢复失败");
            Speeds.Add(speed.Value); var previous = _speed; _speed = speed; return Task.FromResult(previous);
        }
    }

    private sealed class Repository : IGameProfileRepository
    {
        private readonly Dictionary<Guid, GameProfile> _profiles = [];
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<GameProfile>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GameProfile>>([.. _profiles.Values]);
        public Task<GameProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(_profiles.GetValueOrDefault(id));
        public Task UpsertAsync(GameProfile profile, CancellationToken cancellationToken = default)
        { _profiles[profile.Id] = profile; return Task.CompletedTask; }
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        { _profiles.Remove(id); return Task.CompletedTask; }
    }
}
