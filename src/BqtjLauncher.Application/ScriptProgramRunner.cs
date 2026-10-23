using System.Collections.Concurrent;
using BqtjLauncher.Domain;

namespace BqtjLauncher.Application;

/// <summary>解释冻结的语句树；窗口由根运行持有，子方案之间复用，同账号不交错输入。</summary>
internal sealed class ScriptProgramRunner(LauncherModule launcher, IScriptExecutor executor,
    Action<ScriptCommand, ScriptRunState, string> report, Action<Guid, IReadOnlyList<SaveSlot>> scanned,
    ScriptRunControl control, ConcurrentDictionary<int, bool> completed, ConcurrentDictionary<Guid, Guid> knownSessions)
{
    private readonly ConcurrentDictionary<Guid, AccountLease> _leases = new();
    private IReadOnlyList<ScriptCommand> _commands = [];

    public async Task RunAsync(CompiledScript program, CancellationToken token)
    {
        _commands = program.Commands;
        using var parallel = new SemaphoreSlim(program.ConcurrentAccounts == 0
            ? Math.Max(1, program.Commands.Select(command => command.Account.Id).Distinct().Count()) : program.ConcurrentAccounts);
        var begun = false;
        try
        {
            await executor.BeginAsync(token).ConfigureAwait(false); begun = true;
            await ExecuteNodeAsync(program.Root, parallel, token).ConfigureAwait(false);
        }
        finally
        {
            // 失败、停止和正常结束统一交还保留的窗口，不让子方案提前释放其他语句仍要使用的会话。
            foreach (var lease in _leases.Values)
            {
                if (lease.Session.Completion.IsCompleted) continue;
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try { if (lease.Original is { } original) await lease.Session.ApplySpeedAsync(original, cleanup.Token).ConfigureAwait(false); }
                catch (Exception) { report(lease.Last, ScriptRunState.Paused, "窗口保留；原倍率未恢复，请手动调整。"); }
                try { await executor.ReleaseAsync(lease.Session).ConfigureAwait(false); }
                catch (Exception) { report(lease.Last, ScriptRunState.Paused, "窗口保留；视觉绑定释放失败。"); }
                try { await lease.Session.ReleaseWindowLockAsync(cleanup.Token).ConfigureAwait(false); }
                catch (Exception) { report(lease.Last, ScriptRunState.Paused, "窗口保留；尺寸锁未解除，请关闭后重开。"); }
            }
            if (begun) await executor.EndAsync().ConfigureAwait(false);
        }
    }

    private async Task ExecuteNodeAsync(ScriptNode node, SemaphoreSlim parallel, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (node is ScriptCommand command)
        {
            if (completed.ContainsKey(command.Index)) return;
            await control.WaitAsync(token).ConfigureAwait(false);
            await ExecuteCommandAsync(command, parallel, token).ConfigureAwait(false);
            completed[command.Index] = true;
            return;
        }
        var block = (ScriptBlock)node;
        if (block.Parallel)
            await Task.WhenAll(block.Children.Select(child => ExecuteNodeAsync(child, parallel, token))).ConfigureAwait(false);
        else
            foreach (var child in block.Children) await ExecuteNodeAsync(child, parallel, token).ConfigureAwait(false);
    }

    private async Task ExecuteCommandAsync(ScriptCommand command, SemaphoreSlim parallel, CancellationToken token)
    {
        var entered = false;
        try
        {
            var id = command.Account.Id;
            var lease = _leases.GetValueOrDefault(id);
            if (command.Operation == "关闭窗口")
            {
                // 断点继续可能只剩关闭语句；只重新接管本次确实持有过的同一个窗口。
                if (lease is null && knownSessions.TryGetValue(id, out var previous)
                    && (await launcher.GetActiveProfileIdsAsync(token).ConfigureAwait(false)).Contains(id))
                {
                    var retained = await launcher.AcquireAutomationSessionAsync(id, token).ConfigureAwait(false);
                    if (retained is not null)
                    {
                        if (retained.Id != previous)
                        {
                            await retained.ReleaseWindowLockAsync(token).ConfigureAwait(false);
                            throw new InvalidOperationException("账号窗口已被替换，未关闭其他窗口。");
                        }
                        lease = new(retained, command); _leases[id] = lease;
                    }
                }
                if (lease is null) throw new InvalidOperationException("本次运行没有此账号的窗口可关闭。");
                await control.WaitAsync(token).ConfigureAwait(false);
                await executor.ReleaseAsync(lease.Session).ConfigureAwait(false);
                if (!await lease.Session.CloseAsync(token).ConfigureAwait(false))
                    throw new InvalidOperationException("本次窗口已被替换，未关闭其他窗口。");
                _leases.TryRemove(id, out _);
                report(command, ScriptRunState.Completed, "本次账号窗口已关闭");
                return;
            }

            // 按稳定账号ID接管本面板窗口；其他控制者尚未释放时等待，不能交错输入。
            while (lease is null)
            {
                token.ThrowIfCancellationRequested();
                await parallel.WaitAsync(token).ConfigureAwait(false); entered = true;
                await control.WaitAsync(token).ConfigureAwait(false);
                var session = await launcher.AcquireAutomationSessionAsync(id, token).ConfigureAwait(false);
                if (session is null)
                {
                    parallel.Release(); entered = false;
                    report(command, ScriptRunState.Waiting, "账号窗口正在被其他脚本控制，等待释放");
                    await Task.Delay(500, token).ConfigureAwait(false); continue;
                }
                lease = new(session, command);
                _leases[id] = lease;
                lease.Original = await ScriptCoordinator.ApplyWhenReadyAsync(session, SpeedMultiplier.Create(command.Speed), token).ConfigureAwait(false);
                await control.WaitAsync(token).ConfigureAwait(false);
                await ScriptCoordinator.LockWhenReadyAsync(session, token).ConfigureAwait(false);
                var resumed = knownSessions.TryGetValue(id, out var previousId) && previousId == session.Id;
                knownSessions[id] = session.Id;
                if (session.WasExisting && !resumed)
                {
                    // 交还用户后可能手动切过档，旧档号不能当作当前身份；重新从选档确认目标。
                    lease.SaveNumber = await executor.IsInSaveAsync(session, token).ConfigureAwait(false) ? 0 : null;
                    lease.SafeToExit = false;
                }
                else if (resumed)
                {
                    lease.AllowTakeoverSwitch = false;
                    // 中断发生在选档回执之前时，先观察实际是否已入档，再按指定档重建语句。
                    if (lease.SaveNumber is null && await executor.IsInSaveAsync(session, token).ConfigureAwait(false))
                    { lease.SaveNumber = 0; lease.AllowTakeoverSwitch = true; }
                }
            }
            if (!entered) { await parallel.WaitAsync(token).ConfigureAwait(false); entered = true; }
            lease.Last = command;
            await ScriptCoordinator.ApplyWhenReadyAsync(lease.Session, SpeedMultiplier.Create(command.Speed), token).ConfigureAwait(false);
            report(command, ScriptRunState.Running, "执行语句");
            Action<string, ScriptRunState> stage = (value, state) => report(command, state, value);
            await control.WaitAsync(token).ConfigureAwait(false);
            switch (command.Operation)
            {
                case "读取存档名称":
                    await EnsureSelectionAsync(lease, stage, token).ConfigureAwait(false);
                    var slots = await executor.ReadSavesAsync(lease.Session, value => stage(value, ScriptRunState.Running), token).ConfigureAwait(false);
                    scanned(id, slots);
                    break;
                case "启动存档":
                    await EnsureSaveAsync(lease, command.SaveNumber, stage, token).ConfigureAwait(false);
                    break;
                case "日常操作":
                    await EnsureSaveAsync(lease, command.SaveNumber, stage, token).ConfigureAwait(false);
                    lease.SafeToExit = false;
                    await ExecuteVisualAsync(lease, "runOfficialDaily", stage, token).ConfigureAwait(false);
                    lease.SafeToExit = true;
                    await FinishDailyAsync(lease, command, stage, token).ConfigureAwait(false);
                    break;
                case "刷取每日材料":
                    await EnsureSaveAsync(lease, command.SaveNumber, stage, token).ConfigureAwait(false);
                    lease.SafeToExit = false;
                    // 权益来自本次冻结的账号快照，不写入方案常量或复制到每个存档。
                    await executor.FarmDailyMaterialsAsync(lease.Session,
                        command.Farming! with { HasPresident = command.Account.HasPresident }, stage, token).ConfigureAwait(false);
                    break;
                case "退出存档":
                    if (lease.SaveNumber.HasValue)
                    {
                        await executor.ExitSaveAsync(lease.Session, stage, token).ConfigureAwait(false);
                        lease.SaveNumber = null; lease.SafeToExit = false;
                    }
                    break;
                case "旧版脚本":
                    await EnsureSelectionAsync(lease, stage, token).ConfigureAwait(false);
                    lease.SafeToExit = false;
                    await executor.ExecuteAsync(lease.Session, command.LegacyDefinition!,
                        new() { ProfileId = id, AccountName = command.Account.Name, SaveNumber = command.SaveNumber },
                        SpeedMultiplier.Create(command.Speed), stage, token).ConfigureAwait(false);
                    lease.SaveNumber = command.SaveNumber; lease.SafeToExit = command.LegacyDefinition!.Flow.Any(step => step.Type is "officialDaily" or "waitOfficialCompletion");
                    break;
            }
            report(command, ScriptRunState.Completed, command.Operation == "日常操作" ? "官方日常已完成并自动保存，窗口保留" : "语句完成，窗口保留");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { report(command, ScriptRunState.Stopped, "外部操作已停止，游戏内置脚本可能仍在运行"); throw; }
        catch (Exception error) { report(command, ScriptRunState.Paused, error.Message); throw; }
        finally { if (entered) parallel.Release(); }
    }

    /// <summary>跨账号、子方案和并行块都按同账号的下一条操作决定收尾，收尾完成后当前日常才算完成。</summary>
    private async Task FinishDailyAsync(AccountLease lease, ScriptCommand command, Action<string, ScriptRunState> stage, CancellationToken token)
    {
        var next = _commands.FirstOrDefault(item => item.Index > command.Index && item.Account.Id == command.Account.Id
            && !completed.ContainsKey(item.Index));
        if (next is null || next.Operation == "关闭窗口") return;
        if (next.SaveNumber == command.SaveNumber && next.Operation is "日常操作" or "启动存档" or "刷取每日材料")
            await executor.DismissDailyPanelsAsync(lease.Session, stage, token).ConfigureAwait(false);
        else
        {
            stage("日常已保存，退出存档供后续操作使用", ScriptRunState.Running);
            await executor.ExitSaveAsync(lease.Session, stage, token).ConfigureAwait(false);
            lease.SaveNumber = null; lease.SafeToExit = false; lease.AllowTakeoverSwitch = false;
        }
    }

    /// <summary>自动切档只允许已确认完成保存的存档；显式退出是用户单独插入的操作。</summary>
    private async Task EnsureSelectionAsync(AccountLease lease, Action<string, ScriptRunState> stage, CancellationToken token)
    {
        if (!lease.SaveNumber.HasValue) return;
        if (!lease.SafeToExit && !lease.AllowTakeoverSwitch) throw new InvalidOperationException("当前存档尚未确认保存；请先安排日常完成或显式退出存档。");
        if (lease.AllowTakeoverSwitch) stage("接管已有窗口，切换到方案指定存档", ScriptRunState.Running);
        await executor.ExitSaveAsync(lease.Session, stage, token).ConfigureAwait(false);
        lease.SaveNumber = null; lease.SafeToExit = false;
        lease.AllowTakeoverSwitch = false;
    }

    /// <summary>会话已按账号ID绑定；同档日常、刷材料直接衔接，仅档号不同时退出并选档。</summary>
    private async Task EnsureSaveAsync(AccountLease lease, int number, Action<string, ScriptRunState> stage, CancellationToken token)
    {
        if (lease.SaveNumber == number) return;
        await EnsureSelectionAsync(lease, stage, token).ConfigureAwait(false);
        lease.Last = lease.Last with { SaveNumber = number };
        await ExecuteVisualAsync(lease, "enterSave", stage, token).ConfigureAwait(false);
        lease.SaveNumber = number; lease.SafeToExit = false;
    }

    private Task ExecuteVisualAsync(AccountLease lease, string type, Action<string, ScriptRunState> stage, CancellationToken token)
    {
        var definition = new ScriptDefinition
        {
            Id = type,
            Name = type,
            Entry = "entry",
            Flow = [new() { Id = "entry", Type = type, Text = lease.Last.OfficialScriptName ?? "" }]
        };
        return executor.ExecuteAsync(lease.Session, definition,
            new() { ProfileId = lease.Session.ProfileId, SaveNumber = lease.Last.SaveNumber }, SpeedMultiplier.Create(lease.Last.Speed), stage, token);
    }

    private sealed class AccountLease(AutomationGameSession session, ScriptCommand command)
    {
        public AutomationGameSession Session { get; } = session;
        public SpeedMultiplier? Original { get; set; }
        public ScriptCommand Last { get; set; } = command;
        public int? SaveNumber { get => Session.CurrentSave; set => Session.CurrentSave = value; }
        public bool SafeToExit { get => Session.SafeToExit; set => Session.SafeToExit = value; }
        public bool AllowTakeoverSwitch { get; set; } = session.WasExisting;
    }
}
