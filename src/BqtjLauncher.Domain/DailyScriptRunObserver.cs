namespace BqtjLauncher.Domain;

/// <summary>将连续视觉观测送入完成门槛；静止的未完成进度不被当作运行开始。</summary>
public sealed class DailyScriptRunObserver
{
    private readonly Guid _runId;
    private readonly TimeSpan _maximumFrameGap;
    private readonly DailyScriptCompletionGate _gate;
    private DailyScriptProgress? _previous;
    private TimeSpan? _previousCapture;
    private bool _started;
    private DailyScriptProgress? _startedProgress;

    /// <summary>每次执行建立新观测身份；阈值由调用方显式提供，不属于产品默认值。</summary>
    public DailyScriptRunObserver(Guid runId, int requiredSamples, TimeSpan stableDuration, TimeSpan maximumFrameGap)
    {
        _runId = runId;
        _maximumFrameGap = maximumFrameGap;
        _gate = new DailyScriptCompletionGate(runId, requiredSamples, stableDuration, maximumFrameGap);
    }

    /// <summary>用本次看过的未完成进度与后续新鲜进度确认增长；允许直接跳到最后一步。</summary>
    public DailyScriptCompletionResult Observe(TimeSpan capturedAt, TimeSpan now, DailyScriptProgress? progress,
        bool headerCompletionChecked, bool hasFailedItems)
    {
        if (capturedAt < TimeSpan.Zero || now < capturedAt) throw new ArgumentOutOfRangeException(nameof(now));
        var fresh = now - capturedAt <= _maximumFrameGap && (!_previousCapture.HasValue || capturedAt > _previousCapture.Value);
        var valid = progress is not null && progress.Total > 0 && progress.Completed >= 0 && progress.Completed <= progress.Total;
        // 运行建立后出现倒退或总数更换，无法区分OCR错误与用户重新运行，停止当前身份而非继续关闭判断。
        if (_started && fresh && valid && _startedProgress is not null
            && (progress!.Total != _startedProgress.Total || progress.Completed < _startedProgress.Completed))
            throw new InvalidOperationException("进度倒退或总数变化，请重新建立观测运行。");
        // 暂停或中间漏读不抹掉已观察的历史；最终连续绿勾确认仍由gate单独检查帧间隔。
        var advanced = fresh && valid && _previous is not null && _previous.Completed < _previous.Total
            && progress!.Total == _previous.Total && progress.Completed > _previous.Completed;
        if (advanced) _gate.ConfirmObservedProgressAdvance();
        var result = _gate.Observe(new DailyScriptObservation(_runId, capturedAt, false,
            valid ? progress!.Completed : null, valid ? progress!.Total : null, headerCompletionChecked, hasFailedItems), now);
        _started |= result.State != DailyScriptCompletionState.AwaitingStart;
        if (_started && fresh && valid) _startedProgress = progress;
        if (fresh)
        {
            if (valid) _previous = progress;
            _previousCapture = capturedAt;
        }
        return result;
    }
}
