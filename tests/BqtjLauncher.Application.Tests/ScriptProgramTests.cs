using System.Collections.Concurrent;
using System.IO;
using BqtjLauncher.Domain;
using BqtjLauncher.Infrastructure;

namespace BqtjLauncher.Application.Tests;

/// <summary>中文语句、稳定引用与根运行回归；只使用虚构账号、临时库和可控窗口。</summary>
public sealed class ScriptProgramTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bqtj-program-" + Guid.NewGuid().ToString("N"));
    private static ScriptPlan Plan(string source, string name = "测试方案") => new() { Version = 2, Name = name, Source = source };
    private static ScriptAccount Account(string name) => new(Guid.NewGuid(), name, "");
    public ScriptProgramTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void QuotedChineseNamesAndEscapedQuotesKeepArgumentPositions()
    {
        var rows = ScriptSyntax.Parse("# 注释\n日常操作,\"甲,乙\"\"号\",存档5\n\n关闭窗口,\"甲,乙\"\"号\"");
        Assert.Equal(2, rows.Count); Assert.Equal(2, rows[0].Line);
        Assert.Equal("甲,乙\"号", rows[0].Words[1]);
        Assert.Equal(rows[0].Words, ScriptSyntax.Parse(ScriptSyntax.Statement(rows[0].Words)).Single().Words);
    }

    [Theory]
    [InlineData("日常操作,\"未闭合,存档1")]
    [InlineData("并行结束")]
    [InlineData("并行开始\n读取存档名称,甲")]
    [InlineData("不知道,甲")]
    [InlineData("日常操作,甲,存档9")]
    [InlineData("日常操作,甲")]
    [InlineData("日常操作,$缺失,存档1")]
    public void BadSyntaxHasActionableErrorBeforeAnyExecution(string source)
    {
        var account = Account("甲");
        Assert.Throws<InvalidDataException>(() => new ScriptProgramCompiler([], [account], []).Compile(Plan(source)));
    }

    [Fact]
    public void SameNamedAccountsRequireBindingAndRemainBoundAfterRename()
    {
        var a = Account("同名"); var b = Account("同名");
        var plan = Plan("日常操作,同名,存档5");
        Assert.Throws<InvalidDataException>(() => new ScriptProgramCompiler([], [a, b], []).Compile(plan));
        plan.AccountBindings["同名"] = b.Id;
        var program = new ScriptProgramCompiler([], [a, b with { Name = "新名" }], []).Compile(plan);
        Assert.Equal(b.Id, program.Commands.Single().Account.Id);
    }

    [Fact]
    public void ConstantsAndChildCallsCompileIntoFrozenAccountBoundTree()
    {
        var a = Account("甲");
        var child = Plan("日常操作,$主号,$日常一", "子方案");
        child.Constants["主号"] = "甲"; child.Constants["日常一"] = "存档5";
        var root = Plan("调用,子方案");
        var program = new ScriptProgramCompiler([child], [a], []).Compile(root);
        child.Source = "关闭窗口,甲"; child.Constants["日常一"] = "存档8"; root.Source = "";
        var command = Assert.Single(program.Commands);
        Assert.Equal(5, command.SaveNumber); Assert.Contains("子方案", command.Path);
    }

    [Fact]
    public void GlobalDefaultsApplyInsideChildrenAndLocalOverridesStayLocal()
    {
        var account = Account("甲");
        var globals = new Dictionary<string, string> { ["主号"] = "甲", ["日常档"] = "存档5" };
        var child = Plan("日常操作,$主号,$日常档", "子");
        var root = Plan("调用,子\n日常操作,$主号,$日常档");
        root.Constants["日常档"] = "存档6";
        var compiler = new ScriptProgramCompiler([child], [account], [], globalConstants: globals);
        globals["日常档"] = "存档8";
        var compiled = compiler.Compile(root);
        Assert.Collection(compiled.Commands, command => Assert.Equal(5, command.SaveNumber), command => Assert.Equal(6, command.SaveNumber));
        Assert.False(root.Constants.ContainsKey("主号"));
        Assert.Empty(child.Constants);
    }

    [Fact]
    public void GlobalConstantsPersistOutsideTheScriptCatalogAndRejectInvalidReplacement()
    {
        using var files = new ScriptFileStore(_root);
        files.SaveGlobalConstants("主号=甲\n日常档=存档5");
        Assert.Equal("存档5", ScriptSyntax.ParseConstants(files.ReadGlobalConstants())["日常档"]);
        Assert.Empty(files.ListScripts());
        Assert.Throws<InvalidDataException>(() => files.SaveGlobalConstants("错误行"));
        Assert.Contains("日常档=存档5", files.ReadGlobalConstants());
    }

    [Fact]
    public void GlobalAndLocalConstantCyclesFailBeforeExecution()
    {
        var root = Plan("日常操作,甲,$档");
        root.Constants["本地"] = "$档";
        Assert.Throws<InvalidDataException>(() => new ScriptProgramCompiler([], [Account("甲")], [],
            globalConstants: new Dictionary<string, string> { ["档"] = "$本地" }).Compile(root));
    }

    [Fact]
    public void HeaderSettingsPersistAndControlCallsIndependentlyOfDashboardVisibility()
    {
        using var files = new ScriptFileStore(_root);
        var child = Plan("方案设置,子方案,是\n方案设置,面板可见,否\n读取存档名称,甲", "子");
        files.SavePlan(child);
        var restored = Assert.Single(files.ListPlans(out _));
        Assert.True(restored.IsSubPlan); Assert.False(restored.ShowOnDashboard);
        var root = Plan("调用,子");
        Assert.Single(new ScriptProgramCompiler([restored], [Account("甲")], []).Compile(root).Commands);
        restored.Source = restored.Source.Replace("子方案,是", "子方案,否", StringComparison.Ordinal);
        Assert.Contains("未设为子方案", Assert.Throws<InvalidDataException>(() =>
            new ScriptProgramCompiler([restored], [Account("甲")], []).Compile(root)).Message);
    }

    [Theory]
    [InlineData("读取存档名称,甲\n方案设置,子方案,是")]
    [InlineData("方案设置,子方案,是\n方案设置,子方案,否\n读取存档名称,甲")]
    [InlineData("方案设置,面板可见,随便\n读取存档名称,甲")]
    public void InvalidHeadersCanSaveAsDraftButCannotCompile(string source)
    {
        using var files = new ScriptFileStore(_root);
        var plan = Plan(source);
        files.SavePlan(plan);
        Assert.Equal(source, Assert.Single(files.ListPlans(out _)).Source);
        Assert.Throws<InvalidDataException>(() => new ScriptProgramCompiler([], [Account("甲")], []).Compile(plan));
    }
    [Fact]
    public void CyclesAndOverlappingParallelAccountsAreRejected()
    {
        var a = Account("甲");
        var child = Plan("调用,根", "子"); var root = Plan("调用,子", "根");
        Assert.Throws<InvalidDataException>(() => new ScriptProgramCompiler([root, child], [a], []).Compile(root));
        var parallel = Plan("并行开始\n日常操作,甲,存档1\n日常操作,甲,存档2\n并行结束");
        Assert.Contains("同一账号", Assert.Throws<InvalidDataException>(() => new ScriptProgramCompiler([], [a], []).Compile(parallel)).Message);
    }

    [Theory]
    [InlineData(true, "调用,新子")]
    [InlineData(false, "调用,旧子")]
    public void PlanRenameOptionUpdatesBoundReferencesWithoutBreakingOldAliases(bool sync, string expected)
    {
        using var files = new ScriptFileStore(_root);
        var child = Plan("读取存档名称,甲", "旧子");
        var parent = Plan("调用,旧子"); parent.PlanBindings["旧子"] = child.Id;
        files.SavePlan(child); files.SavePlan(parent);
        child.Name = "新子"; files.SavePlan(child, sync);
        var restored = files.ListPlans(out _).Single(plan => plan.Id == parent.Id);
        Assert.Equal(expected, restored.Source);
        var compiled = new ScriptProgramCompiler(files.ListPlans(out _), [Account("甲")], []).Compile(restored);
        Assert.Single(compiled.Commands);
    }

    [Fact]
    public void AccountRenameUpdatesOnlyBoundAccountArgumentsAndPreservesIdentity()
    {
        using var files = new ScriptFileStore(_root);
        var account = Account("旧名");
        var plan = Plan("读取存档名称,旧名\n调用,旧名");
        plan.AccountBindings["旧名"] = account.Id; plan.PlanBindings["旧名"] = Guid.NewGuid();
        files.SavePlan(plan);
        files.RenameAccountReferences(account.Id, "新名");
        var restored = Assert.Single(files.ListPlans(out _));
        Assert.Equal("读取存档名称,新名\n调用,旧名", restored.Source);
        Assert.Equal(account.Id, restored.AccountBindings["新名"]);
    }

    [Fact]
    public async Task SequentialAccountsKeepTextOrderAndReuseWindowsAcrossSaves()
    {
        var f = await FixtureAsync(); await using var launcher = f.Launcher;
        var executor = new Executor(); await using var coordinator = new ScriptCoordinator(launcher, executor);
        var root = Plan("日常操作,甲,存档5\n日常操作,乙,存档1\n日常操作,甲,存档6");
        await coordinator.RunAsync(Compile(root, f.Accounts));
        Assert.Equal(new[] { (f.Accounts[0].Id, 5), (f.Accounts[1].Id, 1), (f.Accounts[0].Id, 6) }, executor.Dailies.ToArray());
        Assert.Equal(2, f.Runtime.Sessions.Count); Assert.Equal(1, executor.ExitCount);
        Assert.All(f.Runtime.Sessions, session => { Assert.Equal(0, session.CloseCount); Assert.False(session.Locked); Assert.Equal(1m, session.Speed.Value); });
        Assert.Equal(1, executor.EndCount);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 1)]
    public async Task ParallelChildrenUseOneRootAndHonorConcurrency(int limit, int maximum)
    {
        var f = await FixtureAsync(); await using var launcher = f.Launcher;
        var executor = new Executor { Barrier = limit == 0 ? 2 : 0 }; await using var coordinator = new ScriptCoordinator(launcher, executor);
        var a = Plan("日常操作,甲,存档5\n日常操作,甲,存档6\n关闭窗口,甲", "甲日常");
        var b = Plan("日常操作,乙,存档1\n关闭窗口,乙", "乙日常");
        var root = Plan("并行开始\n调用,甲日常\n调用,乙日常\n并行结束"); root.ConcurrentAccounts = limit;
        await coordinator.RunAsync(Compile(root, f.Accounts, a, b)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(maximum, executor.Maximum);
        Assert.Equal(2, f.Runtime.Sessions.Count);
        Assert.All(f.Runtime.Sessions, session => Assert.Equal(1, session.CloseCount));
        Assert.Equal(1, executor.BeginCount); Assert.Equal(1, executor.EndCount);
        Assert.All(coordinator.Updates, update => Assert.Equal(ScriptRunState.Completed, update.State));
    }

    [Fact]
    public async Task FailedBranchKeepsItsWindowAndOtherBranchCompletesButParentDoesNotAdvance()
    {
        var f = await FixtureAsync(); await using var launcher = f.Launcher;
        var executor = new Executor { Fail = f.Accounts[0].Id }; await using var coordinator = new ScriptCoordinator(launcher, executor);
        var a = Plan("日常操作,甲,存档5\n关闭窗口,甲", "甲日常");
        var b = Plan("日常操作,乙,存档1\n关闭窗口,乙", "乙日常");
        var root = Plan("并行开始\n调用,甲日常\n调用,乙日常\n并行结束\n日常操作,乙,存档8");
        await coordinator.RunAsync(Compile(root, f.Accounts, a, b));
        Assert.DoesNotContain(executor.Dailies, item => item.Save == 8);
        Assert.Equal(0, f.Runtime.Sessions.Single(session => session.ProfileId == f.Accounts[0].Id).CloseCount);
        Assert.Equal(1, f.Runtime.Sessions.Single(session => session.ProfileId == f.Accounts[1].Id).CloseCount);
        Assert.Contains(coordinator.Updates, update => update.State == ScriptRunState.Paused);
    }

    [Fact]
    public async Task ReadNamesLeavesWindowAndFollowingDailyReusesIt()
    {
        var f = await FixtureAsync(); await using var launcher = f.Launcher;
        var executor = new Executor(); await using var coordinator = new ScriptCoordinator(launcher, executor);
        IReadOnlyList<SaveSlot>? names = null;
        coordinator.SavesRead += (_, slots) => names = slots;
        await coordinator.RunAsync(Compile(Plan("读取存档名称,甲\n日常操作,甲,存档5"), f.Accounts));
        Assert.Equal(8, names!.Count); Assert.Equal(2m, executor.ScanSpeed);
        Assert.Single(f.Runtime.Sessions); Assert.Equal(0, f.Runtime.Sessions[0].CloseCount);
    }

    [Fact]
    public async Task StopReleasesRetainedWindowAndDoesNotRunLaterStatements()
    {
        var f = await FixtureAsync(); await using var launcher = f.Launcher;
        var executor = new Executor { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var coordinator = new ScriptCoordinator(launcher, executor);
        var running = coordinator.RunAsync(Compile(Plan("日常操作,甲,存档1\n关闭窗口,甲"), f.Accounts));
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Stop(); await running;
        Assert.Equal(0, f.Runtime.Sessions[0].CloseCount); Assert.False(f.Runtime.Sessions[0].Locked);
        Assert.Equal(1m, f.Runtime.Sessions[0].Speed.Value); Assert.Equal(1, executor.EndCount);
        Assert.All(coordinator.Updates, update => Assert.Equal(ScriptRunState.Stopped, update.State));
    }

    [Fact]
    public async Task TakesOverManualWindowAndSwitchesToConfiguredSave()
    {
        var f = await FixtureAsync(); await using var launcher = f.Launcher;
        await launcher.StartAsync(f.Accounts[0].Id);
        var manual = f.Runtime.Sessions.Single();
        var executor = new Executor { InSave = true };
        await using var coordinator = new ScriptCoordinator(launcher, executor);
        await coordinator.RunAsync(Compile(Plan("并行开始\n日常操作,甲,存档5\n日常操作,乙,存档1\n并行结束"), f.Accounts));
        Assert.Equal(2, f.Runtime.Sessions.Count);
        Assert.Equal(1, executor.ExitCount);
        Assert.Contains(executor.Dailies, item => item.Account == f.Accounts[0].Id && item.Save == 5);
        Assert.True(manual.ApplyCount > 0); Assert.Equal(0, manual.CloseCount);
        Assert.False(manual.Locked); Assert.Equal(1m, manual.Speed.Value);
    }

    [Fact]
    public async Task NewRunReusesWindowFromStartSaveAndSwitchesItsSave()
    {
        var f = await FixtureAsync(); await using var launcher = f.Launcher;
        var executor = new Executor();
        await using var coordinator = new ScriptCoordinator(launcher, executor);
        await coordinator.RunAsync(Compile(Plan("启动存档,甲,存档3"), f.Accounts));
        executor.InSave = true;
        await coordinator.RunAsync(Compile(Plan("日常操作,甲,存档5"), f.Accounts));
        Assert.Single(f.Runtime.Sessions);
        Assert.Equal(1, executor.ExitCount);
        Assert.Equal(5, Assert.Single(executor.Dailies).Save);
        Assert.All(coordinator.Updates, update => Assert.Equal(ScriptRunState.Completed, update.State));
    }

    [Fact]
    public async Task SameAccountControlIsExclusiveAndReleasedForNextOwner()
    {
        var f = await FixtureAsync(); await using var launcher = f.Launcher;
        var first = (await launcher.AcquireAutomationSessionAsync(f.Accounts[0].Id))!;
        first.CurrentSave = 3;
        Assert.Null(await launcher.AcquireAutomationSessionAsync(f.Accounts[0].Id));
        await first.ReleaseWindowLockAsync();
        var second = (await launcher.AcquireAutomationSessionAsync(f.Accounts[0].Id))!;
        Assert.True(second.WasExisting); Assert.Equal(first.Id, second.Id); Assert.Equal(3, second.CurrentSave);
        await second.ReleaseWindowLockAsync();
        Assert.Single(f.Runtime.Sessions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("日常操作,\"引号未闭合")]
    [InlineData("未知语句,甲")]
    public void IncompleteDraftRoundTripsButCannotCompile(string source)
    {
        using var files = new ScriptFileStore(_root);
        var plan = Plan(source);
        plan.ConstantsSource = "还没写完的常量";
        plan.ShowOnDashboard = false;
        files.SavePlan(plan);
        var restored = Assert.Single(files.ListPlans(out var errors));
        Assert.Empty(errors); Assert.Equal(source, restored.Source);
        Assert.Equal(plan.ConstantsSource, restored.ConstantsSource);
        Assert.False(restored.ShowOnDashboard);
        Assert.Throws<InvalidDataException>(() => new ScriptProgramCompiler([], [Account("甲")], []).Compile(restored));
    }

    [Fact]
    public void BuiltInPlansAreEditableAndDeletedTemplatesDoNotReturnOnInitialization()
    {
        using var files = new ScriptFileStore(_root);
        files.EnsureBuiltInPlans();
        var templates = files.ListPlans(out _);
        Assert.Equal(3, templates.Count);
        Assert.All(templates, plan => { Assert.True(plan.IsBuiltIn); Assert.False(plan.ShowOnDashboard); Assert.EndsWith("（内置模板）", plan.ListDisplayName); });
        var edited = templates[0];
        edited.Name = "用户修改过的模板"; edited.Source = "未完成草稿"; edited.ShowOnDashboard = true;
        files.SavePlan(edited);
        files.DeletePlan(templates[1].Id);
        files.EnsureBuiltInPlans();
        var restored = files.ListPlans(out _);
        Assert.Equal(2, restored.Count);
        var saved = restored.Single(plan => plan.Id == edited.Id);
        Assert.Equal("用户修改过的模板", saved.Name);
        Assert.Equal("未完成草稿", saved.Source);
        Assert.True(saved.ShowOnDashboard);
    }

    [Fact]
    public void RenamingReferenceAlsoWorksWhenAnotherLineIsAnIncompleteDraft()
    {
        using var files = new ScriptFileStore(_root);
        var child = Plan("读取存档名称,甲", "旧名");
        var parent = Plan("调用,旧名\n日常操作,\"还没写完");
        parent.PlanBindings["旧名"] = child.Id;
        files.SavePlan(child); files.SavePlan(parent);
        child.Name = "新名"; files.SavePlan(child, true);
        var restored = files.ListPlans(out _).Single(plan => plan.Id == parent.Id);
        Assert.Equal("调用,新名\n日常操作,\"还没写完", restored.Source);
    }

    [Fact]
    public void HiddenPlansStillWorkAsSubplansAndOldDocumentsDefaultToVisible()
    {
        var account = Account("甲");
        var hidden = Plan("读取存档名称,甲", "隐藏子方案"); hidden.ShowOnDashboard = false;
        var root = Plan("调用,隐藏子方案");
        Assert.Single(Compile(root, [account], hidden).Commands);
        var old = System.Text.Json.JsonSerializer.Deserialize<ScriptPlan>("""{"version":2,"name":"旧文档","source":"读取存档名称,甲"}""", ScriptJson.Options)!;
        Assert.True(old.ShowOnDashboard);
    }

    private static CompiledScript Compile(ScriptPlan root, IReadOnlyList<ScriptAccount> accounts, params ScriptPlan[] children)
        => new ScriptProgramCompiler(children, accounts, []).Compile(root);
    private async Task<(LauncherModule Launcher, Runtime Runtime, ScriptAccount[] Accounts)> FixtureAsync()
    {
        var repository = new SqliteGameProfileRepository(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".db"));
        var runtime = new Runtime(); var launcher = new LauncherModule(repository, runtime);
        await launcher.InitializeAsync();
        var a = await launcher.CreateProfileAsync("甲"); var b = await launcher.CreateProfileAsync("乙");
        return (launcher, runtime, [new(a.Id, a.DisplayName, ""), new(b.Id, b.DisplayName, "")]);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }

    private sealed class Runtime : IAutomationGameRuntime
    {
        public List<Session> Sessions { get; } = [];
        public Task<GamePageResolution> ResolveGamePageAsync() => throw new NotSupportedException();
        public Task<IGameSession> StartAsync(GameProfile profile, CancellationToken token = default)
        { var session = new Session(profile.Id); Sessions.Add(session); return Task.FromResult<IGameSession>(session); }
        public Task<IGameSession> StartAutomationAsync(GameProfile profile, CancellationToken token = default) => StartAsync(profile, token);
    }
    private sealed class Session(Guid profile) : IAutomationGameSession
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid Id { get; } = Guid.NewGuid(); public Guid ProfileId => profile; public Task Completion => _closed.Task;
        public bool Locked { get; private set; } = true; public int CloseCount; public int ApplyCount;
        public SpeedMultiplier Speed { get; private set; } = SpeedMultiplier.Original;
        public void Activate() => throw new InvalidOperationException("脚本不能激活窗口。");
        public Task CloseAsync(CancellationToken token = default) { CloseCount++; _closed.TrySetResult(); return Task.CompletedTask; }
        public Task AcquireWindowLockAsync(CancellationToken token = default) { Locked = true; return Task.CompletedTask; }
        public Task ReleaseWindowLockAsync(CancellationToken token = default) { Locked = false; return Task.CompletedTask; }
        public Task<AutomationWindowTarget> GetTargetAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<SpeedMultiplier> ApplySpeedAsync(SpeedMultiplier speed, CancellationToken token = default)
        { ApplyCount++; var previous = Speed; Speed = speed; return Task.FromResult(previous); }
    }
    private sealed class Executor : IScriptExecutor
    {
        public ConcurrentQueue<(Guid Account, int Save)> Dailies { get; } = new();
        public int Barrier, Maximum, ExitCount, BeginCount, EndCount; public Guid? Fail;
        public decimal ScanSpeed; public TaskCompletionSource? Hold; public bool InSave;
        public Task<bool> IsInSaveAsync(AutomationGameSession session, CancellationToken token) => Task.FromResult(InSave);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        public Task BeginAsync(CancellationToken token) { BeginCount++; return Task.CompletedTask; }
        public Task EndAsync() { EndCount++; return Task.CompletedTask; }
        public Task ReleaseAsync(AutomationGameSession session) => Task.CompletedTask;
        public async Task ExecuteAsync(AutomationGameSession session, ScriptDefinition definition, ScriptPlanTask task, SpeedMultiplier speed, Action<string, ScriptRunState> stage, CancellationToken token)
        {
            if (definition.Flow[0].Type != "runOfficialDaily") return;
            Dailies.Enqueue((session.ProfileId, task.SaveNumber)); Entered.TrySetResult();
            if (Fail == session.ProfileId) throw new InvalidOperationException("虚构失败");
            lock (this) { _active++; Maximum = Math.Max(Maximum, _active); if (_active >= Barrier) _both.TrySetResult(); }
            try
            {
                if (Barrier > 0) await _both.Task.WaitAsync(token);
                if (Hold is not null) await Hold.Task.WaitAsync(token);
                await Task.Delay(40, token);
            }
            finally { lock (this) _active--; }
        }
        public Task ExitSaveAsync(AutomationGameSession session, Action<string, ScriptRunState> stage, CancellationToken token)
        { ExitCount++; return Task.CompletedTask; }
        public Task<IReadOnlyList<SaveSlot>> ReadSavesAsync(AutomationGameSession session, Action<string> stage, CancellationToken token)
        { return ReadAsync(session, token); }
        private async Task<IReadOnlyList<SaveSlot>> ReadAsync(AutomationGameSession session, CancellationToken token)
        {
            ScanSpeed = (await session.ApplySpeedAsync(SpeedMultiplier.Create(2), token)).Value;
            return Enumerable.Range(1, 8).Select(number => new SaveSlot(number, "虚构档" + number)).ToArray();
        }
    }
}
