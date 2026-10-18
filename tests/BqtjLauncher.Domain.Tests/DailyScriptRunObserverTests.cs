namespace BqtjLauncher.Domain.Tests;

/// <summary>连续观测回归覆盖静止/旧完成画面、真实进度增长与运行身份失效。</summary>
public sealed class DailyScriptRunObserverTests
{
    private static DailyScriptRunObserver Create() => new(Guid.NewGuid(), 3, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
    private static DailyScriptCompletionResult Feed(DailyScriptRunObserver observer, double time, int done, int total = 22, bool saved = false)
        => observer.Observe(TimeSpan.FromSeconds(time), TimeSpan.FromSeconds(time), new(done, total), saved, false);

    [Theory]
    [InlineData(22, true)]
    [InlineData(3, false)]
    public void StaticScenesCannotEstablishStart(int done, bool saved)
    {
        var observer = Create();
        for (var i = 0; i < 6; i++) Assert.Equal(DailyScriptCompletionState.AwaitingStart, Feed(observer, i, done, saved: saved).State);
    }

    [Fact]
    public void ObservedProgressThenStableSaveCompletesVisually()
    {
        var observer = Create();
        Feed(observer, 0, 22, saved: true);
        Feed(observer, 1, 0);
        Assert.Equal(DailyScriptCompletionState.Running, Feed(observer, 2, 1).State);
        Feed(observer, 3, 22, saved: true);
        Feed(observer, 4, 22, saved: true);
        Assert.Equal(DailyScriptCompletionState.VisualComplete, Feed(observer, 5, 22, saved: true).State);
    }

    [Fact]
    public void GapOrMissingProgressBreaksStartEvidence()
    {
        var observer = Create();
        Feed(observer, 0, 1);
        Assert.Equal(DailyScriptCompletionState.AwaitingStart, Feed(observer, 5, 2).State);
        observer.Observe(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(6), null, false, false);
        Assert.Equal(DailyScriptCompletionState.AwaitingStart, Feed(observer, 7, 3).State);
    }

    [Fact]
    public void StaleFrameCannotEstablishStart()
    {
        var observer = Create();
        Feed(observer, 0, 1);
        Assert.Equal(DailyScriptCompletionState.AwaitingStart,
            observer.Observe(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8), new(2, 22), false, false).State);
    }

    [Theory]
    [InlineData(0, 22)]
    [InlineData(2, 35)]
    public void StartedRunRejectsResetOrChangedTotal(int done, int total)
    {
        var observer = Create();
        Feed(observer, 0, 0); Feed(observer, 1, 1);
        Assert.Throws<InvalidOperationException>(() => Feed(observer, 2, done, total));
    }

    [Fact]
    public void MissingFrameCannotHideResetOfStartedRun()
    {
        var observer = Create();
        Feed(observer, 0, 0); Feed(observer, 1, 1);
        observer.Observe(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), null, false, false);
        Assert.Throws<InvalidOperationException>(() => Feed(observer, 8, 0));
    }

    [Fact]
    public void ReachingCompletionWithoutIntermediateAdvanceIsInsufficient()
    {
        var observer = Create();
        Feed(observer, 0, 0, 1);
        Assert.Equal(DailyScriptCompletionState.AwaitingStart, Feed(observer, 1, 1, 1, true).State);
    }
}
