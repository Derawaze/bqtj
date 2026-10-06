namespace BqtjLauncher.Application.Tests;

/// <summary>首页只有点击读取存档才弹出选择页；覆盖实际缺失的过渡，而不是预先准备弹窗。</summary>
public sealed class DailySaveEntryFlowTests
{
    [Fact]
    public async Task OpensReadSaveFromHomeBeforeWaitingForSelection()
    {
        var dialogVisible = false;
        var clicks = 0;
        await DailySaveEntryFlow.EnsureAsync(_ => Task.FromResult(new DailySaveEntryObservation(dialogVisible, !dialogVisible)),
            _ => { clicks++; dialogVisible = true; return Task.CompletedTask; }, TimeSpan.FromSeconds(1), default);
        Assert.True(dialogVisible);
        Assert.Equal(1, clicks);
    }

    [Fact]
    public async Task ExistingSelectionDoesNotClickButtonBehindModal()
    {
        var clicks = 0;
        await DailySaveEntryFlow.EnsureAsync(_ => Task.FromResult(new DailySaveEntryObservation(true, true)),
            _ => { clicks++; return Task.CompletedTask; }, TimeSpan.FromSeconds(1), default);
        Assert.Equal(0, clicks);
    }

    [Fact]
    public async Task SlowDialogDoesNotRepeatReadClickAndCanBeCancelled()
    {
        using var stop = new CancellationTokenSource();
        var observations = 0;
        var clicks = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DailySaveEntryFlow.EnsureAsync(_ =>
        {
            if (++observations == 3) stop.Cancel();
            return Task.FromResult(new DailySaveEntryObservation(false, true));
        }, _ => { clicks++; return Task.CompletedTask; }, TimeSpan.FromSeconds(2), stop.Token));
        Assert.Equal(1, clicks);
    }
}
