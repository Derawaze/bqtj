using System.Diagnostics;

namespace BqtjLauncher.Application;

/// <summary>只来自同一截图内标题与全部卡片校验的选档坐标。</summary>
public sealed record DailySaveSlot(int X, int Y);

/// <summary>等待弹窗完整绘制，不重试任何输入；连续新帧给出相同坐标后才允许选档。</summary>
public static class DailySaveLayoutFlow
{
    public static async Task<DailySaveSlot> WaitAsync(Func<CancellationToken, Task<DailySaveSlot?>> inspect,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        DailySaveSlot? previous = null;
        while (timer.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await inspect(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // 只等待布局未就绪；身份、截图、桌面等异常直接向外传，不吞掉安全校验失败。
            if (timer.Elapsed >= timeout) break;
            if (current is not null && current == previous) return current;
            previous = current;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        throw new DailyScriptExecutionException("等待存档选择", DailyScriptFailureCode.SaveLayout);
    }
}