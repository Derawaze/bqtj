using System.Diagnostics;

namespace BqtjLauncher.Application;

/// <summary>同一新截图中的首页入口或存档弹窗；文字与点击坐标由视觉组件保留。</summary>
public sealed record DailySaveEntryObservation(bool SelectionVisible, bool ReadButtonVisible);

/// <summary>登录后的首页过渡：已有弹窗直接继续，否则只点击一次“读取存档”再等弹窗。</summary>
public static class DailySaveEntryFlow
{
    public static async Task EnsureAsync(Func<CancellationToken, Task<DailySaveEntryObservation>> inspect,
        Func<CancellationToken, Task> clickRead, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var clickedRead = false;
        while (timer.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var view = await inspect(cancellationToken).ConfigureAwait(false);
            if (view.SelectionVisible) return;
            if (view.ReadButtonVisible && !clickedRead)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await clickRead(cancellationToken).ConfigureAwait(false);
                clickedRead = true;
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        throw new DailyScriptExecutionException("等待存档选择", DailyScriptFailureCode.MenuNotFound);
    }
}
