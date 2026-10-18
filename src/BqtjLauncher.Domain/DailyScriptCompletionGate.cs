namespace BqtjLauncher.Domain;

/// <summary>一次截图的官方脚本观测；时间由真实单调时钟提供，不使用Flash倍速计时。</summary>
public sealed record DailyScriptObservation(
    Guid RunId,
    TimeSpan CapturedAt,
    bool RunningSignal,
    int? CompletedSteps,
    int? TotalSteps,
    bool HeaderCompletionChecked,
    bool HasFailedItems);

/// <summary>只表示画面证据的阶段；VisualComplete不等于服务端保存已持久化。</summary>
public enum DailyScriptCompletionState { AwaitingStart, Running, ConfirmingCompletion, VisualComplete }

/// <summary>带运行身份的判定结果；中间红叉只用于提示，不阻止官方脚本完成。</summary>
public sealed record DailyScriptCompletionResult(Guid RunId, DailyScriptCompletionState State, bool HasFailedItems);

/// <summary>防止旧完成画面、单帧绿勾或其他账号观测被当作本次完成；不执行关闭或切档。</summary>
public sealed class DailyScriptCompletionGate
{
    private readonly Guid _runId;
    private readonly int _requiredSamples;
    private readonly TimeSpan _stableDuration;
    private readonly TimeSpan _maximumFrameGap;
    private TimeSpan? _lastCapture;
    private TimeSpan? _firstCompletionCapture;
    private int _completionSamples;
    private int? _completionTotal;
    private bool _started;
    private bool _hasFailedItems;
    private DailyScriptCompletionState _state;

    /// <summary>稳定样本数、持续时间与最大帧间隔必须显式配置，正式值须经实机验证。</summary>
    public DailyScriptCompletionGate(Guid runId, int requiredSamples, TimeSpan stableDuration, TimeSpan maximumFrameGap)
    {
        if (runId == Guid.Empty) throw new ArgumentException("运行身份不能为空。", nameof(runId));
        ArgumentOutOfRangeException.ThrowIfLessThan(requiredSamples, 2);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stableDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumFrameGap, TimeSpan.Zero);
        _runId = runId;
        _requiredSamples = requiredSamples;
        _stableDuration = stableDuration;
        _maximumFrameGap = maximumFrameGap;
    }

    /// <summary>接收当前运行的新观测；不完整、过期或乱序的画面会撤销完成确认。</summary>
    public DailyScriptCompletionResult Observe(DailyScriptObservation observation, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.RunId != _runId)
            throw new ArgumentException("观测不属于当前运行，禁止混用其他账号或存档。", nameof(observation));
        if (now < TimeSpan.Zero || observation.CapturedAt < TimeSpan.Zero || now < observation.CapturedAt)
            throw new ArgumentOutOfRangeException(nameof(now), "必须使用同一真实单调时钟。");
        if (now - observation.CapturedAt > _maximumFrameGap
            || (_lastCapture.HasValue && observation.CapturedAt <= _lastCapture.Value))
        {
            ResetCompletionConfirmation();
            return Result();
        }
        if (_lastCapture.HasValue && observation.CapturedAt - _lastCapture.Value > _maximumFrameGap)
            ResetCompletionConfirmation();
        _lastCapture = observation.CapturedAt;

        var validProgress = observation.TotalSteps is > 0 && observation.CompletedSteps is >= 0
            && observation.CompletedSteps <= observation.TotalSteps;
        // 必须看见本次尚未完成的运行阶段；仅点击启动按钮或看到旧的N/N不能建立开始证据。
        if (!_started && validProgress && observation.RunningSignal && observation.CompletedSteps < observation.TotalSteps)
        {
            _started = true;
            _state = DailyScriptCompletionState.Running;
        }
        if (!_started) return Result();
        _hasFailedItems |= observation.HasFailedItems;
        if (!validProgress || observation.CompletedSteps != observation.TotalSteps || !observation.HeaderCompletionChecked)
        {
            ResetCompletionConfirmation();
            return Result();
        }

        // 总步数可因用户内置脚本而变化，但稳定确认期间不能混用两套不同的进度总数。
        if (_completionTotal != observation.TotalSteps) ResetCompletionConfirmation();
        _completionTotal = observation.TotalSteps;
        _firstCompletionCapture ??= observation.CapturedAt;
        _completionSamples++;
        _state = _completionSamples >= _requiredSamples && observation.CapturedAt - _firstCompletionCapture.Value >= _stableDuration
            ? DailyScriptCompletionState.VisualComplete : DailyScriptCompletionState.ConfirmingCompletion;
        return Result();
    }

    /// <summary>读取决策前重新检查证据是否过期，避免无新截图时保留旧的完成结论。</summary>
    public DailyScriptCompletionResult GetCurrent(TimeSpan now)
    {
        if (now < TimeSpan.Zero || (_lastCapture.HasValue && now < _lastCapture.Value))
            throw new ArgumentOutOfRangeException(nameof(now));
        if (_lastCapture.HasValue && now - _lastCapture.Value > _maximumFrameGap) ResetCompletionConfirmation();
        return Result();
    }

    private DailyScriptCompletionResult Result() => new(_runId, _state, _hasFailedItems);

    private void ResetCompletionConfirmation()
    {
        _firstCompletionCapture = null;
        _completionTotal = null;
        _completionSamples = 0;
        _state = _started ? DailyScriptCompletionState.Running : DailyScriptCompletionState.AwaitingStart;
    }
}
