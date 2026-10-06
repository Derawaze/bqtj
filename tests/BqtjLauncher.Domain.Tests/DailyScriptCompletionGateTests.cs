namespace BqtjLauncher.Domain.Tests;

/// <summary>完成判定回归使用虚构观测，重点防止误关闭、跨运行串用和将中间红叉当失败。</summary>
public sealed class DailyScriptCompletionGateTests
{
    private readonly Guid _runId = Guid.NewGuid();
    private DailyScriptCompletionGate Create() => new(_runId, 3, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    private DailyScriptObservation Frame(double time, int? done, int? total, bool saved = false, bool running = false, bool failed = false)
        => new(_runId, TimeSpan.FromSeconds(time), running, done, total, saved, failed);
    private static DailyScriptCompletionResult Feed(DailyScriptCompletionGate gate, DailyScriptObservation frame)
        => gate.Observe(frame, frame.CapturedAt);
    private void Start(DailyScriptCompletionGate gate, int total = 22) => Feed(gate, Frame(0, 0, total, running: true));

    [Fact]
    public void OldCompletedSceneDoesNotEstablishThisRunStarted()
    {
        var gate = Create();
        for (var i = 0; i < 5; i++)
            Assert.Equal(DailyScriptCompletionState.AwaitingStart, Feed(gate, Frame(i, 22, 22, saved: true, running: true)).State);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(22)]
    [InlineData(35)]
    public void CompletionRequiresStableFinalSaveAndSupportsVariableTotal(int total)
    {
        var gate = Create();
        Start(gate, total);
        Assert.Equal(DailyScriptCompletionState.ConfirmingSave, Feed(gate, Frame(1, total, total, saved: true)).State);
        Assert.Equal(DailyScriptCompletionState.ConfirmingSave, Feed(gate, Frame(2, total, total, saved: true)).State);
        Assert.Equal(DailyScriptCompletionState.VisualComplete, Feed(gate, Frame(3, total, total, saved: true)).State);
    }

    [Fact]
    public void IntermediateFailuresRemainWarningWhenFinalSaveCompletes()
    {
        var gate = Create();
        Start(gate);
        Feed(gate, Frame(0.5, 12, 22, failed: true));
        Feed(gate, Frame(1, 22, 22, saved: true));
        Feed(gate, Frame(2, 22, 22, saved: true));
        var result = Feed(gate, Frame(3, 22, 22, saved: true));
        Assert.Equal(DailyScriptCompletionState.VisualComplete, result.State);
        Assert.True(result.HasFailedItems);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(21, 22, true)]
    [InlineData(22, 22, false)]
    [InlineData(23, 22, true)]
    [InlineData(0, 0, true)]
    public void MissingOrInvalidSaveEvidenceNeverCompletes(int? done, int? total, bool saved)
    {
        var gate = Create();
        Start(gate);
        for (var i = 1; i < 5; i++)
            Assert.Equal(DailyScriptCompletionState.Running, Feed(gate, Frame(i, done, total, saved)).State);
    }

    [Fact]
    public void LostCheckmarkRestartsStableConfirmation()
    {
        var gate = Create();
        Start(gate);
        Feed(gate, Frame(1, 22, 22, saved: true));
        Feed(gate, Frame(2, 22, 22));
        Feed(gate, Frame(3, 22, 22, saved: true));
        Assert.Equal(DailyScriptCompletionState.ConfirmingSave, Feed(gate, Frame(4, 22, 22, saved: true)).State);
        Assert.Equal(DailyScriptCompletionState.VisualComplete, Feed(gate, Frame(5, 22, 22, saved: true)).State);
    }

    [Fact]
    public void DuplicateOrOldFrameCannotCountAsNewEvidence()
    {
        var gate = Create();
        Start(gate);
        Feed(gate, Frame(1, 22, 22, saved: true));
        Feed(gate, Frame(1, 22, 22, saved: true));
        Feed(gate, Frame(2, 22, 22, saved: true));
        Assert.Equal(DailyScriptCompletionState.ConfirmingSave, Feed(gate, Frame(3, 22, 22, saved: true)).State);
    }

    [Fact]
    public void StaleFramesAndLongGapsRestartConfirmation()
    {
        var gate = Create();
        Start(gate);
        Feed(gate, Frame(1, 22, 22, saved: true));
        Assert.Equal(DailyScriptCompletionState.Running, gate.Observe(Frame(2, 22, 22, saved: true), TimeSpan.FromSeconds(8)).State);
        Assert.Equal(DailyScriptCompletionState.ConfirmingSave, Feed(gate, Frame(9, 22, 22, saved: true)).State);
    }

    [Fact]
    public void DifferentRunIsRejectedWithoutChangingCurrentRun()
    {
        var gate = Create();
        Start(gate);
        Assert.Throws<ArgumentException>(() => Feed(gate, Frame(1, 22, 22, saved: true) with { RunId = Guid.NewGuid() }));
        Assert.Equal(DailyScriptCompletionState.Running, gate.GetCurrent(TimeSpan.FromSeconds(1)).State);
    }

    [Fact]
    public void ChangedTotalCannotReuseEarlierSaveConfirmation()
    {
        var gate = Create();
        Start(gate);
        Feed(gate, Frame(1, 22, 22, saved: true));
        Feed(gate, Frame(2, 22, 22, saved: true));
        Assert.Equal(DailyScriptCompletionState.ConfirmingSave, Feed(gate, Frame(3, 35, 35, saved: true)).State);
    }

    [Fact]
    public void EnoughSamplesWithoutRealElapsedTimeDoNotComplete()
    {
        var gate = Create();
        Start(gate);
        Feed(gate, Frame(0.1, 22, 22, saved: true));
        Feed(gate, Frame(0.2, 22, 22, saved: true));
        Assert.Equal(DailyScriptCompletionState.ConfirmingSave, Feed(gate, Frame(0.3, 22, 22, saved: true)).State);
    }

    [Fact]
    public void CompletedDecisionExpiresWithoutFreshScreenshots()
    {
        var gate = Create();
        Start(gate);
        Feed(gate, Frame(1, 22, 22, saved: true));
        Feed(gate, Frame(2, 22, 22, saved: true));
        Feed(gate, Frame(3, 22, 22, saved: true));
        Assert.Equal(DailyScriptCompletionState.Running, gate.GetCurrent(TimeSpan.FromSeconds(6)).State);
    }
}
