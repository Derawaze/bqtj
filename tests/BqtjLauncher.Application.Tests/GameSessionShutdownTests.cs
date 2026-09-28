using System.Diagnostics;
using System.Windows.Threading;
using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

/// <summary>模拟面板 OnExit 同步等待关闭：子进程退出后，关闭任务不能再等待已停泵的界面线程。</summary>
public sealed class GameSessionShutdownTests
{
    [Theory]
    [InlineData(500)]
    [InlineData(30000)]
    public async Task CloseCompletesWhenCallerStopsPumpingDispatcher(int childDelayMilliseconds)
    {
        var verdict = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                using var process = Process.Start(new ProcessStartInfo("powershell.exe")
                {
                    Arguments = $"-NoProfile -NonInteractive -Command Start-Sleep -Milliseconds {childDelayMilliseconds}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })!;
                var session = new FlashGameProcessSession(Guid.NewGuid(), process);
                try
                {
                    // 短任务正常退出，长任务由生产关闭逻辑在5秒后结束；两条续体都不能依赖UI。
                    var completed = session.CloseAsync().Wait(TimeSpan.FromSeconds(8));
                    verdict.SetResult(completed && process.HasExited);
                }
                finally
                {
                    // 回归失败时也只清理本测试创建的子进程，避免诊断自身留下残留。
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) { verdict.SetException(exception); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(await verdict.Task.WaitAsync(TimeSpan.FromSeconds(12)),
            "子进程已退出，但关闭任务仍等待停止处理消息的 WPF 线程，造成残留进程。");
    }
}
