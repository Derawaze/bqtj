using BqtjLauncher.Domain;

namespace BqtjLauncher.Application;

public enum DailyScriptRunStatus { Skipped, Completed, Cancelled, Paused }

/// <summary>仅传输阶段与结构化识别证据，不包含凭据、截图或原始OCR文字。</summary>
public sealed record DailyScriptFrame(Guid SessionId, Guid RunId, long CapturedAt,
    DailyScriptProgress? Progress, bool FinalSaveChecked, bool HasFailedItems);

public sealed record DailyScriptRunResult(Guid RunId, DailyScriptRunStatus Status, bool HasFailedItems, string? Detail = null)
{
    /// <summary>倍率恢复失败必须显式提示用户，不把收尾异常静默记成正常恢复。</summary>
    public bool NeedsManualSpeedRestore { get; internal set; }
    public bool WorkerCleanupFailed { get; internal set; }
}

/// <summary>独立视觉组件负责存档→助理→脚本→运行确认，再持续提供新截图证据；取消须终止自身输入。</summary>
public interface IDailyScriptExecutor
{
    IAsyncEnumerable<DailyScriptFrame> ExecuteAsync(AutomationWindowTarget target, int saveNumber,
        Guid runId, CancellationToken cancellationToken);
}

/// <summary>单账号单存档的外层生命周期；完成门槛留在面板侧，不信任工作进程的“成功”字符串。</summary>
public sealed class DailyScriptRunner(LauncherModule launcher, IDailyScriptExecutor executor, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>新建并绑定会话，临时原速运行；观察到本次稳定保存才关闭，失败和取消保留窗口。</summary>
    public Task<DailyScriptRunResult> RunAsync(Guid profileId, int saveNumber,
        CancellationToken cancellationToken = default)
        => RunAsync(profileId, saveNumber, SpeedMultiplier.Original, cancellationToken);

    /// <summary>菜单阶段保持原速，确实观察到本次进度增长后才应用方案倍率。</summary>
    public async Task<DailyScriptRunResult> RunAsync(Guid profileId, int saveNumber, SpeedMultiplier scriptSpeed,
        CancellationToken cancellationToken = default)
    {
        _ = SpeedMultiplier.Create(scriptSpeed.Value);
        ArgumentOutOfRangeException.ThrowIfLessThan(saveNumber, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(saveNumber, 8);
        var runId = Guid.NewGuid();
        var session = await launcher.StartAutomationSessionAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (session is null) return new(runId, DailyScriptRunStatus.Skipped, false);
        SpeedMultiplier? previousSpeed = null;
        var warning = false;
        var speedApplied = false;
        var stage = "等待本会话目标";
        DailyScriptRunResult? outcome = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(60));
        var token = timeout.Token;
        var epoch = _clock.GetTimestamp();
        var observer = new DailyScriptRunObserver(runId, 3, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
        IAsyncEnumerator<DailyScriptFrame>? frames = null;
        try
        {
            // 容器先提供目标不代表已登录或进入存档；这些视觉就绪条件由执行器确认。
            var target = await ReadTargetAsync(session, token).ConfigureAwait(false);
            if (target.SessionId != session.Id || target.ProfileId != profileId || target.NativeProcessId <= 0
                || target.ContainerProcessId <= 0 || target.FlashWindowHandle <= 0 || target.Width <= 0 || target.Height <= 0)
                throw new InvalidOperationException("自动化目标与新建会话不符。");
            stage = "设置菜单操作原速";
            previousSpeed = await AwaitBoundAsync(session.ApplySpeedAsync(SpeedMultiplier.Original, token), session, token).ConfigureAwait(false);
            stage = "进入存档并运行内置脚本";
            frames = executor.ExecuteAsync(target, saveNumber, runId, token).GetAsyncEnumerator(token);
            while (await AwaitBoundAsync(frames.MoveNextAsync().AsTask(), session, token).ConfigureAwait(false))
            {
                var frame = frames.Current;
                if (frame.SessionId != session.Id || frame.RunId != runId || frame.CapturedAt < epoch)
                    throw new InvalidOperationException("观测来自其他会话、旧运行或旧截图。");
                var result = observer.Observe(_clock.GetElapsedTime(epoch, frame.CapturedAt), _clock.GetElapsedTime(epoch),
                    frame.Progress, frame.FinalSaveChecked, frame.HasFailedItems);
                warning |= result.HasFailedItems;
                if (!speedApplied && result.State != DailyScriptCompletionState.AwaitingStart)
                {
                    if (!scriptSpeed.IsOriginal)
                        await AwaitBoundAsync(session.ApplySpeedAsync(scriptSpeed, token), session, token).ConfigureAwait(false);
                    speedApplied = true;
                }
                if (result.State != DailyScriptCompletionState.VisualComplete) continue;
                stage = "保存完成后关闭本次会话";
                token.ThrowIfCancellationRequested();
                if (!await session.CloseAsync(token).ConfigureAwait(false))
                    return outcome = new(runId, DailyScriptRunStatus.Paused, warning, "本次会话已退出或被替换。");
                return outcome = new(runId, DailyScriptRunStatus.Completed, warning);
            }
            // 工作进程退出或宣称结束不能替代最终保存的连续证据。
            return outcome = new(runId, DailyScriptRunStatus.Paused, warning, "视觉组件已结束，但尚未确认本次保存完成。");
        }
        catch (OperationCanceledException)
        { return outcome = new(runId, cancellationToken.IsCancellationRequested ? DailyScriptRunStatus.Cancelled : DailyScriptRunStatus.Paused,
            warning, cancellationToken.IsCancellationRequested ? "已停止外层任务，游戏内置脚本可能仍在运行。" : "阶段超时：" + stage); }
        catch (DailyScriptExecutionException exception)
        { return outcome = new(runId, DailyScriptRunStatus.Paused, warning, exception.Describe()); }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.IO.IOException or TimeoutException or ArgumentException)
        { return outcome = new(runId, DailyScriptRunStatus.Paused, warning, "阶段未通过：" + stage); }
        finally
        {
            timeout.Cancel();
            // 工作进程与倍率恢复均有独立收尾上限，不能阻塞面板退出；不关闭保留的游戏窗口。
            if (frames is not null)
            {
                try { await frames.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8), CancellationToken.None).ConfigureAwait(false); }
                catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or System.IO.IOException or InvalidOperationException)
                { if (outcome is not null) outcome.WorkerCleanupFailed = true; }
            }
            if (previousSpeed.HasValue && !session.Completion.IsCompleted)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try { await session.ApplySpeedAsync(previousSpeed.Value, cleanup.Token).WaitAsync(cleanup.Token).ConfigureAwait(false); }
                catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or System.IO.IOException)
                { if (outcome is not null) outcome.NeedsManualSpeedRestore = true; }
            }
        }
    }

    /// <summary>只重试无副作用的目标读取；不重复任何存档、运行或确认点击。</summary>
    private static async Task<AutomationWindowTarget> ReadTargetAsync(AutomationGameSession session, CancellationToken token)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
        startup.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            try { return await AwaitBoundAsync(session.GetTargetAsync(startup.Token), session, startup.Token).ConfigureAwait(false); }
            catch (InvalidOperationException) when (!session.Completion.IsCompleted)
            { await Task.Delay(250, startup.Token).ConfigureAwait(false); }
        }
    }

    private static async Task<T> AwaitBoundAsync<T>(Task<T> operation, AutomationGameSession session, CancellationToken token)
    {
        var finished = await Task.WhenAny(operation, session.Completion).WaitAsync(token).ConfigureAwait(false);
        if (finished == session.Completion || session.Completion.IsCompleted)
            throw new InvalidOperationException("本次游戏会话已退出。");
        return await operation.WaitAsync(token).ConfigureAwait(false);
    }
}
