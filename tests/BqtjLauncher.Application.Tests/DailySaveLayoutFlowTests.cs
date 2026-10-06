namespace BqtjLauncher.Application.Tests;

/// <summary>重放弹窗过渡期间的未绘制或位移帧；持续失配仍拒绝选档。</summary>
public sealed class DailySaveLayoutFlowTests
{
    [Fact]
    public async Task WaitsForCompleteLayoutAndTwoConsistentFrames()
    {
        var point = new DailySaveSlot(375, 351);
        var samples = new Queue<DailySaveSlot?>([null, point, point]);
        var result = await DailySaveLayoutFlow.WaitAsync(_ => Task.FromResult(samples.Dequeue()),
            TimeSpan.FromSeconds(2), default);
        Assert.Equal(point, result);
        Assert.Empty(samples);
    }

    [Fact]
    public async Task MovingOrMissingFrameResetsStability()
    {
        var point = new DailySaveSlot(375, 351);
        var samples = new Queue<DailySaveSlot?>([new(375, 360), point, null, point, point]);
        var result = await DailySaveLayoutFlow.WaitAsync(_ => Task.FromResult(samples.Dequeue()),
            TimeSpan.FromSeconds(2), default);
        Assert.Equal(point, result);
        Assert.Empty(samples);
    }

    [Fact]
    public async Task PersistentMismatchTimesOutWithoutCoordinate()
    {
        var failure = await Assert.ThrowsAsync<DailyScriptExecutionException>(() => DailySaveLayoutFlow.WaitAsync(
            _ => Task.FromResult<DailySaveSlot?>(null), TimeSpan.FromMilliseconds(150), default));
        Assert.Equal(DailyScriptFailureCode.SaveLayout, failure.Code);
    }

    [Fact]
    public async Task IdentityFailureIsNotRetriedAsLayoutMismatch()
    {
        var inspections = 0;
        var failure = await Assert.ThrowsAsync<DailyScriptExecutionException>(() => DailySaveLayoutFlow.WaitAsync(_ =>
        {
            inspections++;
            throw new DailyScriptExecutionException("等待存档选择", DailyScriptFailureCode.TargetChanged);
        }, TimeSpan.FromSeconds(1), default));
        Assert.Equal(DailyScriptFailureCode.TargetChanged, failure.Code);
        Assert.Equal(1, inspections);
    }

    [Fact]
    public async Task CancelAfterFirstFrameDoesNotReturnSingleFrameCoordinate()
    {
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DailySaveLayoutFlow.WaitAsync(_ =>
        {
            stop.Cancel();
            return Task.FromResult<DailySaveSlot?>(new(375, 351));
        }, TimeSpan.FromSeconds(1), stop.Token));
    }
}