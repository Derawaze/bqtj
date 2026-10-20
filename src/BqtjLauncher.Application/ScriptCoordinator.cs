using System.Collections.Concurrent;
using BqtjLauncher.Domain;

namespace BqtjLauncher.Application;

/// <summary>共享方案调度及扫描生命周期；文本方案可互斥接管本面板持有的游戏会话。</summary>
public sealed class ScriptCoordinator(LauncherModule launcher, IScriptExecutor executor) : IAsyncDisposable
{
    private CancellationTokenSource? _stop;
    private Task _running = Task.CompletedTask;
    private readonly ConcurrentDictionary<int, ScriptRunUpdate> _updates = new();
    public bool IsRunning => !_running.IsCompleted;
    public IReadOnlyList<ScriptRunUpdate> Updates => _updates.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
    public event EventHandler? Changed;
    public event Action<Guid, IReadOnlyList<SaveSlot>>? SavesRead;
    public string RunningName { get; private set; } = "";

    /// <summary>固定本次配置及图片快照；目录编辑不改变已经执行中的任务。</summary>
    public Task RunAsync(ScriptPlan plan, ScriptDefinition definition, CancellationToken token = default)
    {
        if (IsRunning) throw new InvalidOperationException("已有方案正在运行，请先停止队列。");
        ScriptJson.Validate(plan);
        if (plan.ScriptId != definition.Id) throw new InvalidOperationException("方案对应的脚本不存在。");
        RunningName = plan.Name;
        var snapshot = ScriptJson.Clone(plan);
        var script = ScriptJson.Clone(definition);
        script.Directory = definition.Directory;
        foreach (var step in script.Flow.Where(step => step.Type is "waitImage" or "clickImage"))
            script.Images[step.Image] = File.ReadAllBytes(Path.Combine(script.Directory, step.Image));
        _stop?.Dispose();
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _updates.Clear();
        foreach (var (task, index) in snapshot.Tasks.Select((task, index) => (task, index)))
            Report(task, index, ScriptRunState.Waiting, "等待执行");
        _running = Task.Run(() => RunCoreAsync(snapshot, script, _stop.Token), CancellationToken.None);
        Changed?.Invoke(this, EventArgs.Empty);
        return _running;
    }

    /// <summary>文本方案的唯一运行入口；编辑与改名不修改已编译的本次快照。</summary>
    public Task RunAsync(CompiledScript program, CancellationToken token = default)
    {
        if (IsRunning) throw new InvalidOperationException("已有方案正在运行，请先停止本次运行。");
        _stop?.Dispose(); _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        RunningName = program.Name;
        _updates.Clear();
        foreach (var command in program.Commands) ReportCommand(command, ScriptRunState.Waiting, "等待执行");
        _running = Task.Run(async () =>
        {
            try
            {
                var runner = new ScriptProgramRunner(launcher, executor, ReportCommand,
                    (profile, slots) => SavesRead?.Invoke(profile, slots));
                await runner.RunAsync(program, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                foreach (var command in program.Commands.Where(command => _updates[command.Index].State is ScriptRunState.Waiting or ScriptRunState.Running or ScriptRunState.Paused))
                    ReportCommand(command, ScriptRunState.Stopped, "本次运行已停止，游戏内置脚本可能仍在运行");
            }
            catch (Exception error)
            {
                foreach (var command in program.Commands.Where(command => _updates[command.Index].State == ScriptRunState.Waiting))
                    ReportCommand(command, ScriptRunState.Paused, "本语句未执行：" + error.Message);
            }
            finally { Changed?.Invoke(this, EventArgs.Empty); }
        }, CancellationToken.None);
        Changed?.Invoke(this, EventArgs.Empty);
        return _running;
    }

    private void ReportCommand(ScriptCommand command, ScriptRunState state, string stage)
    {
        _updates[command.Index] = new(command.Account.Id, command.Index, command.Label, state, stage);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Stop() => _stop?.Cancel();

    private async Task RunCoreAsync(ScriptPlan plan, ScriptDefinition definition, CancellationToken token)
    {
        var begun = false;
        try
        {
            await executor.BeginAsync(token).ConfigureAwait(false);
            begun = true;
            var groups = plan.Tasks.Select((task, index) => (task, index)).GroupBy(item => item.task.ProfileId).ToArray();
            using var parallel = new SemaphoreSlim(plan.ConcurrentAccounts == 0 ? groups.Length : plan.ConcurrentAccounts);
            await Task.WhenAll(groups.Select(group => RunAccountAsync(group.ToArray(), definition, plan.Speed, parallel, token)))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            foreach (var update in Updates.Where(update => update.State is ScriptRunState.Waiting or ScriptRunState.Running or ScriptRunState.Paused))
                Report(plan.Tasks[update.TaskIndex], update.TaskIndex, ScriptRunState.Stopped, "队列已停止，内置脚本可能仍在运行");
        }
        catch (Exception error)
        {
            foreach (var update in Updates.Where(update => update.State == ScriptRunState.Waiting))
                Report(plan.Tasks[update.TaskIndex], update.TaskIndex, ScriptRunState.Paused, error.Message);
        }
        finally
        {
            if (begun) await executor.EndAsync().ConfigureAwait(false);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>账号占用等待不占并发槽；本账号异常仅结束其队列，其他账号继续。</summary>
    private async Task RunAccountAsync((ScriptPlanTask task, int index)[] tasks, ScriptDefinition definition,
        decimal speed, SemaphoreSlim parallel, CancellationToken token)
    {
        AutomationGameSession? session = null;
        SpeedMultiplier? original = null;
        var acquired = false;
        var current = tasks[0];
        try
        {
            while (!acquired)
            {
                token.ThrowIfCancellationRequested();
                if ((await launcher.GetActiveProfileIdsAsync(token).ConfigureAwait(false)).Contains(current.task.ProfileId))
                {
                    Report(current.task, current.index, ScriptRunState.Waiting, "账号已有游戏窗口，等待空闲");
                    await Task.Delay(500, token).ConfigureAwait(false);
                    continue;
                }
                await parallel.WaitAsync(token).ConfigureAwait(false);
                acquired = true;
                session = await launcher.StartAutomationSessionAsync(current.task.ProfileId, token).ConfigureAwait(false);
                if (session is null) { parallel.Release(); acquired = false; await Task.Delay(500, token).ConfigureAwait(false); }
            }

            for (var position = 0; position < tasks.Length; position++)
            {
                current = tasks[position];
                if (session is null)
                {
                    // 中间项勾选关闭后，仍等待账号空闲，不接管用户在间隙新开的窗口。
                    while ((session = await launcher.StartAutomationSessionAsync(current.task.ProfileId, token).ConfigureAwait(false)) is null)
                    {
                        Report(current.task, current.index, ScriptRunState.Waiting, "等待账号空闲");
                        await Task.Delay(500, token).ConfigureAwait(false);
                    }
                }
                original ??= await ApplyWhenReadyAsync(session, SpeedMultiplier.Create(speed), token).ConfigureAwait(false);
                Report(current.task, current.index, ScriptRunState.Running, "准备指定存档");
                await executor.ExecuteAsync(session, definition, current.task, SpeedMultiplier.Create(speed),
                    (stage, state) => Report(current.task, current.index, state, stage), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (current.task.CloseAfterFinish)
                {
                    await executor.ReleaseAsync(session).ConfigureAwait(false);
                    if (!await session.CloseAsync(token).ConfigureAwait(false))
                        throw new InvalidOperationException("本次会话已退出或被替换，未关闭其他窗口。");
                    session = null;
                    original = null;
                }
                else if (position + 1 < tasks.Length)
                {
                    await executor.ExitSaveAsync(session,
                        (stage, state) => Report(current.task, current.index, state, stage), token).ConfigureAwait(false);
                }
                Report(current.task, current.index, ScriptRunState.Completed,
                    current.task.CloseAfterFinish ? "已确认完成并关闭窗口" : "已确认完成");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            foreach (var item in tasks.Where(item => !_updates.TryGetValue(item.index, out var update) || update.State != ScriptRunState.Completed))
                Report(item.task, item.index, ScriptRunState.Stopped, "队列已停止，内置脚本可能仍在运行");
        }
        catch (Exception error)
        {
            Report(current.task, current.index, ScriptRunState.Paused, error.Message);
            foreach (var item in tasks.Where(item => item.index > current.index))
                Report(item.task, item.index, ScriptRunState.Paused, "前一存档未完成，等待人工处理");
        }
        finally
        {
            if (session is not null)
            {
                if (original.HasValue && !session.Completion.IsCompleted)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    try { await session.ApplySpeedAsync(original.Value, cleanup.Token).ConfigureAwait(false); }
                    catch (Exception) { Report(current.task, current.index, ScriptRunState.Paused, "窗口保留；倍率未能恢复，请手动调整"); }
                }
                await ReleaseRetainedWindowAsync(session,
                    message => Report(current.task, current.index, ScriptRunState.Paused, message)).ConfigureAwait(false);
            }
            if (acquired) parallel.Release();
        }
    }

    internal static async Task<SpeedMultiplier> ApplyWhenReadyAsync(AutomationGameSession session, SpeedMultiplier speed, CancellationToken token)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
        startup.CancelAfter(TimeSpan.FromSeconds(60));
        while (!session.Completion.IsCompleted)
        {
            try { return await session.ApplySpeedAsync(speed, startup.Token).ConfigureAwait(false); }
            // 冷启动时容器调度器可能忙于加载，控制服务会先结束超时连接。
            // 倍率是幂等设置，可在原60秒启动预算内重试；游戏点击不经过这里。
            catch (Exception error) when (error is InvalidOperationException or EndOfStreamException)
            { await Task.Delay(250, startup.Token).ConfigureAwait(false); }
        }
        throw new InvalidOperationException("本次游戏窗口已关闭。");
    }

    /// <summary>接管后校准原生尺寸，加载过程中尚不可用时只等待，不重启游戏。</summary>
    internal static async Task LockWhenReadyAsync(AutomationGameSession session, CancellationToken token)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
        startup.CancelAfter(TimeSpan.FromSeconds(60));
        while (!session.Completion.IsCompleted)
        {
            try { await session.AcquireWindowLockAsync(startup.Token).ConfigureAwait(false); return; }
            catch (Exception error) when (error is InvalidOperationException or EndOfStreamException)
            { await Task.Delay(250, startup.Token).ConfigureAwait(false); }
        }
        throw new InvalidOperationException("游戏窗口已关闭。");
    }

    /// <summary>扫描可复用首页窗口；首次配置可保留窗口，取消选档也不破坏用户现场。</summary>
    public async Task<IReadOnlyList<SaveSlot>> ReadSavesAsync(Guid profileId, Action<string> stage, CancellationToken token, bool keepWindow = false)
    {
        var begun = false;
        AutomationGameSession? session = null;
        SpeedMultiplier? original = null;
        try
        {
            await executor.BeginAsync(token).ConfigureAwait(false);
            begun = true;
            while ((session = await launcher.AcquireAutomationSessionAsync(profileId, token).ConfigureAwait(false)) is null)
            { stage("账号窗口正在被其他脚本控制，等待释放"); await Task.Delay(500, token).ConfigureAwait(false); }
            original = await ApplyWhenReadyAsync(session, SpeedMultiplier.Create(2), token).ConfigureAwait(false);
            await LockWhenReadyAsync(session, token).ConfigureAwait(false);
            if (session.WasExisting && await executor.IsInSaveAsync(session, token).ConfigureAwait(false))
            {
                if (!keepWindow) throw new InvalidOperationException("当前窗口已进入存档，请返回读取存档页面后再读取名称。");
                stage("接管已有窗口，退出当前存档后读取名称");
                await executor.ExitSaveAsync(session, (message, _) => stage(message), token).ConfigureAwait(false);
                session.CurrentSave = null; session.SafeToExit = false;
            }
            var slots = await executor.ReadSavesAsync(session, stage, token).ConfigureAwait(false);
            await executor.ReleaseAsync(session).ConfigureAwait(false);
            if (!keepWindow && !session.WasExisting) await session.CloseAsync(token).ConfigureAwait(false);
            return slots;
        }
        finally
        {
            if (session is not null)
            {
                if (original.HasValue && !session.Completion.IsCompleted)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    try { await session.ApplySpeedAsync(original.Value, cleanup.Token).ConfigureAwait(false); }
                    catch (Exception) { stage("窗口保留；倍率未能恢复，请手动调整"); }
                }
                await ReleaseRetainedWindowAsync(session, stage).ConfigureAwait(false);
            }
            if (begun) await executor.EndAsync().ConfigureAwait(false);
        }
    }

    /// <summary>释放视觉绑定后解除保留窗口的尺寸锁；成功、取消和异常路径共用，不关闭用户现场。</summary>
    private async Task ReleaseRetainedWindowAsync(AutomationGameSession session, Action<string> stage)
    {
        try { await executor.ReleaseAsync(session).ConfigureAwait(false); }
        finally
        {
            if (!session.Completion.IsCompleted)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try { await session.ReleaseWindowLockAsync(cleanup.Token).ConfigureAwait(false); }
                catch (Exception) { stage("窗口保留；尺寸锁未能解除，请关闭该窗口后重新启动"); }
            }
        }
    }
    private void Report(ScriptPlanTask task, int index, ScriptRunState state, string stage)
    {
        _updates[index] = new(task.ProfileId, index, task.Label, state, stage);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await _running.ConfigureAwait(false);
        _stop?.Dispose();
    }
}
