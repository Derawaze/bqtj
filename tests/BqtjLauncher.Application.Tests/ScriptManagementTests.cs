using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using BqtjLauncher.Domain;
using BqtjLauncher.Infrastructure;

namespace BqtjLauncher.Application.Tests;

/// <summary>脚本配置与账号队列回归，仅使用临时虚构库和可控会话，不启动真实游戏。</summary>
public sealed class ScriptManagementTests : IDisposable
{
    private static readonly int[] SavedOrder = [8, 2];
    private static readonly int[] RunOrder = [3, 1];
    private static readonly decimal[] TemporarySpeeds = [2m, 1m];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bqtj-script-" + Guid.NewGuid().ToString("N"));
    private static ScriptDefinition Definition => new() { Id = "official-daily", Name = "虚构日常", Entry = "daily", Flow = [new() { Id = "daily", Type = "officialDaily" }] };
    public ScriptManagementTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void CatalogContinuesAfterBrokenNullAndDuplicateDefinitions()
    {
        using var files = new ScriptFileStore(_root);
        File.WriteAllText(Path.Combine(_root, "valid.json"), JsonSerializer.Serialize(Definition, ScriptJson.Options));
        File.WriteAllText(Path.Combine(_root, "broken.json"), "{");
        File.WriteAllText(Path.Combine(_root, "null.json"), """{"version":1,"id":"null","name":"坏脚本","entry":"a","flow":null}""");
        var catalog = files.ListScripts();
        Assert.Equal(3, catalog.Count);
        Assert.Single(catalog, item => item.Error is null);
        File.Copy(Path.Combine(_root, "valid.json"), Path.Combine(_root, "duplicate.json"));
        Assert.Equal(2, files.ListScripts().Count(item => item.Error == "脚本标识重复，请修改id。"));
    }

    [Fact]
    public void PlanRoundTripPreservesOrderCloseFlagsSpeedAndStableIdentities()
    {
        using var files = new ScriptFileStore(_root);
        var account = Guid.NewGuid();
        var plan = Plan(Task(account, 8, false), Task(account, 2, true));
        plan.Speed = 2; plan.ConcurrentAccounts = 0;
        files.SavePlan(plan);
        var loaded = Assert.Single(files.ListPlans(out var errors));
        Assert.Empty(errors);
        Assert.Equal(SavedOrder, loaded.Tasks.Select(task => task.SaveNumber));
        Assert.False(loaded.Tasks[0].CloseAfterFinish);
        Assert.Equal(account, loaded.Tasks[0].ProfileId);
        var text = File.ReadAllText(Directory.GetFiles(Path.Combine(_root, "plans"), "*.json").Single());
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("username", text, StringComparison.OrdinalIgnoreCase);
        files.DeletePlan(plan.Id);
        Assert.Empty(files.ListPlans(out _));
    }

    [Fact]
    public void SameNamesAreAllowedButSameAccountSaveIsNotDuplicated()
    {
        var plan = Plan(Task(Guid.NewGuid(), 1, true), Task(Guid.NewGuid(), 1, true));
        ScriptJson.Validate(plan);
        plan.Tasks.Add(ScriptJson.Clone(plan.Tasks[0]));
        Assert.Throws<InvalidDataException>(() => ScriptJson.Validate(plan));
    }

    [Theory]
    [InlineData("shell")]
    [InlineData("python")]
    [InlineData("exe")]
    public void ArbitraryProgramStepsAreRejected(string type)
    {
        var definition = Definition;
        definition.Flow[0].Type = type;
        Assert.Throws<InvalidDataException>(() => ScriptFileStore.ValidateDefinition(definition));
    }

    [Fact]
    public void ImageCannotEscapeScriptPackage()
    {
        var definition = Definition;
        definition.Directory = _root;
        Assert.Throws<InvalidDataException>(() => ScriptFileStore.ResolveImage(definition, "../other.png"));
    }

    [Fact]
    public async Task ParallelAccountsKeepTheirOwnSaveOrderAndReuseNonClosedSession()
    {
        var fixture = await FixtureAsync(2);
        await using var launcher = fixture.Launcher;
        var executor = new FakeExecutor { BarrierAccounts = 2 };
        await using var coordinator = new ScriptCoordinator(launcher, executor);
        var a = fixture.Profiles[0].Id; var b = fixture.Profiles[1].Id;
        await coordinator.RunAsync(Plan(Task(a, 3, false), Task(b, 8, true), Task(a, 1, true)), Definition).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RunOrder, executor.Executed.Where(item => item.Account == a).Select(item => item.Save));
        Assert.Equal(1, executor.ExitCount);
        Assert.Equal(2, executor.MaximumActive);
        Assert.Single(fixture.Runtime.Sessions, session => session.ProfileId == a);
        Assert.All(fixture.Runtime.Sessions, session => Assert.Equal(1, session.CloseCount));
    }

    [Fact]
    public async Task CloseIsIssuedOnlyAfterExecutorCompletes()
    {
        var fixture = await FixtureAsync(1);
        await using var launcher = fixture.Launcher;
        var executor = new FakeExecutor { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var coordinator = new ScriptCoordinator(launcher, executor);
        var run = coordinator.RunAsync(Plan(Task(fixture.Profiles[0].Id, 1, true)), Definition);
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, fixture.Runtime.Sessions.Single().CloseCount);
        executor.Hold.SetResult();
        await run;
        Assert.Equal(1, fixture.Runtime.Sessions.Single().CloseCount);
    }

    [Fact]
    public async Task FailedAccountRetainsWindowAndDoesNotBlockOtherAccount()
    {
        var fixture = await FixtureAsync(2);
        await using var launcher = fixture.Launcher;
        var a = fixture.Profiles[0].Id; var b = fixture.Profiles[1].Id;
        var executor = new FakeExecutor { FailAccount = a };
        await using var coordinator = new ScriptCoordinator(launcher, executor);
        await coordinator.RunAsync(Plan(Task(a, 1, true), Task(a, 2, true), Task(b, 1, true)), Definition);
        Assert.Equal(0, fixture.Runtime.Sessions.Single(session => session.ProfileId == a).CloseCount);
        Assert.Equal(1, fixture.Runtime.Sessions.Single(session => session.ProfileId == b).CloseCount);
        Assert.All(coordinator.Updates.Where(update => update.ProfileId == a), update => Assert.Equal(ScriptRunState.Paused, update.State));
        Assert.Equal(1m, fixture.Runtime.Sessions.Single(session => session.ProfileId == a).Speed.Value);
    }

    [Fact]
    public async Task BusyManualAccountWaitsWithoutActivationWhileOtherAccountRuns()
    {
        var fixture = await FixtureAsync(2);
        await using var launcher = fixture.Launcher;
        var a = fixture.Profiles[0].Id; var b = fixture.Profiles[1].Id;
        await launcher.StartAsync(a);
        var manual = fixture.Runtime.Sessions.Single();
        var executor = new FakeExecutor();
        await using var coordinator = new ScriptCoordinator(launcher, executor);
        var run = coordinator.RunAsync(Plan(Task(a, 1, true), Task(b, 1, true)), Definition);
        await UntilAsync(() => coordinator.Updates.Any(update => update.ProfileId == b && update.State == ScriptRunState.Completed));
        Assert.Equal(0, manual.ActivateCount);
        Assert.Equal(0, manual.CloseCount);
        Assert.DoesNotContain(executor.Executed, item => item.Account == a);
        await manual.CloseAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(executor.Executed, item => item.Account == a);
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(2, 2)]
    public async Task ParallelismIsConfigurableWithoutFixedFourAccountLimit(int limit, int expected)
    {
        var fixture = await FixtureAsync(8);
        await using var launcher = fixture.Launcher;
        var executor = new FakeExecutor { BarrierAccounts = expected };
        await using var coordinator = new ScriptCoordinator(launcher, executor);
        var plan = Plan(fixture.Profiles.Select(profile => Task(profile.Id, 1, true)).ToArray());
        plan.ConcurrentAccounts = limit;
        await coordinator.RunAsync(plan, Definition).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(expected, executor.MaximumActive);
        Assert.Equal(8, executor.Executed.Count);
    }

    [Fact]
    public async Task StopKeepsWindowRestoresSpeedAndEndsVisionLease()
    {
        var fixture = await FixtureAsync(1);
        await using var launcher = fixture.Launcher;
        var executor = new FakeExecutor { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var coordinator = new ScriptCoordinator(launcher, executor);
        var run = coordinator.RunAsync(Plan(Task(fixture.Profiles[0].Id, 1, true)), Definition);
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        coordinator.Stop(); await run;
        Assert.Equal(0, fixture.Runtime.Sessions.Single().CloseCount);
        Assert.Equal(1m, fixture.Runtime.Sessions.Single().Speed.Value);
        Assert.False(fixture.Runtime.Sessions.Single().WindowLocked);
        Assert.Equal(executor.BeginCount, executor.EndCount);
        Assert.Equal(ScriptRunState.Stopped, coordinator.Updates.Single().State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedWindowUnlocksAfterCompletionOrFailure(bool fail)
    {
        var fixture = await FixtureAsync(1);
        await using var launcher = fixture.Launcher;
        var account = fixture.Profiles[0].Id;
        var executor = new FakeExecutor
        {
            FailAccount = fail ? account : null,
            OnExecute = () => Assert.True(fixture.Runtime.Sessions.Single().WindowLocked)
        };
        await using var coordinator = new ScriptCoordinator(launcher, executor);
        await coordinator.RunAsync(Plan(Task(account, 1, false)), Definition);
        Assert.False(fixture.Runtime.Sessions.Single().WindowLocked);
        Assert.Equal(0, fixture.Runtime.Sessions.Single().CloseCount);
        Assert.Equal(fail ? ScriptRunState.Paused : ScriptRunState.Completed, coordinator.Updates.Single().State);
    }
    [Fact]
    public async Task EditingPlanDuringRunDoesNotChangeSnapshot()
    {
        var fixture = await FixtureAsync(1);
        await using var launcher = fixture.Launcher;
        var executor = new FakeExecutor { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var coordinator = new ScriptCoordinator(launcher, executor);
        var plan = Plan(Task(fixture.Profiles[0].Id, 3, false));
        var run = coordinator.RunAsync(plan, Definition);
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        plan.Tasks[0].SaveNumber = 8; plan.Tasks[0].CloseAfterFinish = true; plan.Speed = 10;
        executor.Hold.SetResult(); await run;
        Assert.Equal(3, executor.Executed.Single().Save);
        Assert.Equal(0, fixture.Runtime.Sessions.Single().CloseCount);
        Assert.Equal(TemporarySpeeds, fixture.Runtime.Sessions.Single().SpeedChanges);
    }

    [Fact]
    public async Task ScanReadsAllEightSavesAndClosesOnlyScanSession()
    {
        var fixture = await FixtureAsync(1);
        await using var launcher = fixture.Launcher;
        var executor = new FakeExecutor { OnScan = () => Assert.Equal(2m, fixture.Runtime.Sessions.Single().Speed.Value) };
        await using var coordinator = new ScriptCoordinator(launcher, executor);
        var result = await coordinator.ReadSavesAsync(fixture.Profiles[0].Id, _ => { }, default);
        Assert.Equal(Enumerable.Range(1, 8), result.Select(slot => slot.Number));
        Assert.Equal(1, fixture.Runtime.Sessions.Single().CloseCount);
        Assert.Equal(1, executor.EndCount);
    }

    [Fact]
    public async Task ScanFailureUnlocksRetainedWindowAndEndsVisionLease()
    {
        var fixture = await FixtureAsync(1);
        await using var launcher = fixture.Launcher;
        var executor = new FakeExecutor { FailScan = true };
        await using var coordinator = new ScriptCoordinator(launcher, executor);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ReadSavesAsync(fixture.Profiles[0].Id, _ => { }, default));
        Assert.False(fixture.Runtime.Sessions.Single().WindowLocked);
        Assert.Equal(TemporarySpeeds, fixture.Runtime.Sessions.Single().SpeedChanges);
        Assert.Equal(0, fixture.Runtime.Sessions.Single().CloseCount);
        Assert.Equal(executor.BeginCount, executor.EndCount);
    }
    private static ScriptPlanTask Task(Guid account, int save, bool close) => new()
    { ProfileId = account, AccountName = "同名虚构账号", SaveNumber = save, SaveName = "虚构档", CloseAfterFinish = close };
    private static ScriptPlan Plan(params ScriptPlanTask[] tasks) => new() { Name = "虚构测试方案", Tasks = [.. tasks] };
    private async Task<(LauncherModule Launcher, FakeRuntime Runtime, List<GameProfile> Profiles)> FixtureAsync(int count)
    {
        var repository = new SqliteGameProfileRepository(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".db"));
        var runtime = new FakeRuntime();
        var launcher = new LauncherModule(repository, runtime);
        await launcher.InitializeAsync();
        var profiles = new List<GameProfile>();
        for (var index = 0; index < count; index++) profiles.Add(await launcher.CreateProfileAsync("虚构账号" + index));
        return (launcher, runtime, profiles);
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await System.Threading.Tasks.Task.Delay(10, timeout.Token);
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }

    private sealed class FakeRuntime : IAutomationGameRuntime
    {
        public List<FakeSession> Sessions { get; } = [];
        public Task<GamePageResolution> ResolveGamePageAsync() => throw new NotSupportedException();
        public Task<IGameSession> StartAsync(GameProfile profile, CancellationToken token = default)
        { var session = new FakeSession(profile.Id); Sessions.Add(session); return System.Threading.Tasks.Task.FromResult<IGameSession>(session); }
        public Task<IGameSession> StartAutomationAsync(GameProfile profile, CancellationToken token = default) => StartAsync(profile, token);
    }
    private sealed class FakeSession(Guid profile) : IAutomationGameSession
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid Id { get; } = Guid.NewGuid();
        public Guid ProfileId => profile;
        public Task Completion => _closed.Task;
        public int CloseCount { get; private set; }
        public int ActivateCount { get; private set; }
        public SpeedMultiplier Speed { get; private set; } = SpeedMultiplier.Original;
        public List<decimal> SpeedChanges { get; } = [];
        public void Activate() => ActivateCount++;
        public Task CloseAsync(CancellationToken token = default) { CloseCount++; _closed.TrySetResult(); return System.Threading.Tasks.Task.CompletedTask; }
        public bool WindowLocked { get; private set; } = true;
        public Task AcquireWindowLockAsync(CancellationToken token = default) => System.Threading.Tasks.Task.CompletedTask;
        public Task ReleaseWindowLockAsync(CancellationToken token = default) { WindowLocked = false; return System.Threading.Tasks.Task.CompletedTask; }
        public Task<AutomationWindowTarget> GetTargetAsync(CancellationToken token = default) => System.Threading.Tasks.Task.FromResult(new AutomationWindowTarget(Id, profile, 1, 2, 3, 950, 600));
        public Task<SpeedMultiplier> ApplySpeedAsync(SpeedMultiplier speed, CancellationToken token = default)
        { var previous = Speed; Speed = speed; SpeedChanges.Add(speed.Value); return System.Threading.Tasks.Task.FromResult(previous); }
    }
    private sealed class FakeExecutor : IScriptExecutor
    {
        public int BeginCount, EndCount, ExitCount, MaximumActive, BarrierAccounts;
        private int _active;
        private readonly TaskCompletionSource _barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid? FailAccount;
        public Action? OnExecute;
        public bool FailScan;
        public Action? OnScan;
        public TaskCompletionSource? Hold;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<(Guid Account, int Save)> Executed { get; } = new();
        public Task BeginAsync(CancellationToken token) { BeginCount++; return System.Threading.Tasks.Task.CompletedTask; }
        public Task EndAsync() { EndCount++; return System.Threading.Tasks.Task.CompletedTask; }
        public Task ReleaseAsync(AutomationGameSession session) => System.Threading.Tasks.Task.CompletedTask;
        public Task<IReadOnlyList<SaveSlot>> ReadSavesAsync(AutomationGameSession session, Action<string> stage, CancellationToken token)
        {
            OnScan?.Invoke();
            if (FailScan) throw new InvalidOperationException("虚构扫描失败");
            return System.Threading.Tasks.Task.FromResult<IReadOnlyList<SaveSlot>>(Enumerable.Range(1, 8).Select(number => new SaveSlot(number, "测试档" + number)).ToArray());
        }
        public Task ExitSaveAsync(AutomationGameSession session, Action<string, ScriptRunState> stage, CancellationToken token)
        { ExitCount++; return System.Threading.Tasks.Task.CompletedTask; }
        public async Task ExecuteAsync(AutomationGameSession session, ScriptDefinition definition, ScriptPlanTask task,
            SpeedMultiplier speed, Action<string, ScriptRunState> stage, CancellationToken token)
        {
            OnExecute?.Invoke();
            Executed.Enqueue((task.ProfileId, task.SaveNumber));
            lock (this) { _active++; MaximumActive = Math.Max(MaximumActive, _active); if (_active >= BarrierAccounts) _barrier.TrySetResult(); }
            Entered.TrySetResult();
            try
            {
                if (FailAccount == task.ProfileId) throw new InvalidOperationException("虚构失败");
                if (BarrierAccounts > 0) await _barrier.Task.WaitAsync(token);
                if (Hold is not null) await Hold.Task.WaitAsync(token);
                await System.Threading.Tasks.Task.Delay(20, token);
            }
            finally { lock (this) _active--; }
        }
    }
}
