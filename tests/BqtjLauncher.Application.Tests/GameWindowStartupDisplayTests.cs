using System.Windows;
using System.Windows.Threading;
using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

/// <summary>用真实 WPF 首次显示重现全屏启动冲突，不创建账号、宿主或读取运行偏好。</summary>
[Collection(nameof(WpfUiTestGroup))]
public sealed class GameWindowStartupDisplayTests
{
    /// <summary>验证两个首次显示档位，并在窗口与 Dispatcher 清理完毕后报告结果。</summary>
    [Theory]
    [InlineData(WindowState.Normal, false)]
    [InlineData(WindowState.Maximized, true)]
    public async Task FirstShowKeepsConfiguredModeAndTransparentLoading(
        WindowState initialState,
        bool expectedActivation)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                try
                {
                    window = new Window
                    {
                        WindowStyle = WindowStyle.None,
                        WindowState = initialState,
                        ShowInTaskbar = false,
                        Width = 100,
                        Height = 100,
                    };
                    // 执行 App 中使用的同一个首次显示操作，而非只断言一份布尔计算副本。
                    GameWindowStartupDisplay.ShowLoading(window);
                    Assert.True(window.IsVisible);
                    Assert.Equal(initialState, window.WindowState);
                    Assert.Equal(0, window.Opacity);
                    Assert.Equal(expectedActivation, window.ShowActivated);
                }
                finally
                {
                    window?.Close();
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
                // 下一档必须等前一档的窗口和消息分发器清理完毕，避免跨 STA 线程重叠。
                completed.SetResult(true);
            }
            catch (Exception exception) { completed.SetException(exception); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // 此处验证显示行为，不限制共享 CI 主机的 WPF 冷启动耗时；仍保留有限挂死等待。
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
