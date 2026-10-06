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

    /// <summary>每次人工开始观测使用新身份；阈值由调用方显式提供，不属于产品默认值。</summary>
    public DailyScriptRunObserver(Guid runId, int requiredSamples, TimeSpan stableDuration, TimeSpan maximumFrameGap)
    {
        _runId = runId;
        _maximumFrameGap = maximumFrameGap;
        _gate = new DailyScriptCompletionGate(runId, requiredSamples, stableDuration, maximumFrameGap);
    }

    /// <summary>只有新鲜的同总数进度实际增长，且仍未完成，才建立本次开始证据。</summary>
    public DailyScriptCompletionResult Observe(TimeSpan capturedAt, TimeSpan now, DailyScriptProgress? progress,
        bool finalSaveChecked, bool hasFailedItems)
    {
        if (capturedAt < TimeSpan.Zero || now < capturedAt) throw new ArgumentOutOfRangeException(nameof(now));
        var fresh = now - capturedAt <= _maximumFrameGap && (!_previousCapture.HasValue || capturedAt > _previousCapture.Value);
        var continuous = fresh && _previousCapture.HasValue && capturedAt - _previousCapture.Value <= _maximumFrameGap;
        var valid = progress is not null && progress.Total > 0 && progress.Completed >= 0 && progress.Completed <= progress.Total;
        // 运行建立后出现倒退或总数更换，无法区分OCR错误与用户重新运行，停止当前身份而非继续关闭判断。
        if (_started && fresh && valid && _startedProgress is not null
            && (progress!.Total != _startedProgress.Total || progress.Completed < _startedProgress.Completed))
            throw new InvalidOperationException("进度倒退或总数变化，请重新建立观测运行。");
        var running = continuous && valid && _previous is not null && progress!.Total == _previous.Total
            && progress.Completed > _previous.Completed && progress.Completed < progress.Total;
        var result = _gate.Observe(new DailyScriptObservation(_runId, capturedAt, running,
            valid ? progress!.Completed : null, valid ? progress!.Total : null, finalSaveChecked, hasFailedItems), now);
        _started |= result.State != DailyScriptCompletionState.AwaitingStart;
        if (_started && fresh && valid) _startedProgress = progress;
        if (fresh)
        {
            _previous = valid ? progress : null;
            _previousCapture = capturedAt;
        }
        else _previous = null;
        return result;
    }
}
