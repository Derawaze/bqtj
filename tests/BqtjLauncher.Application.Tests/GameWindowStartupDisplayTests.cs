using System.Windows;
using System.Windows.Threading;
using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

/// <summary>用真实 WPF 首次显示重现全屏启动冲突，不创建账号、宿主或读取运行偏好。</summary>
public sealed class GameWindowStartupDisplayTests
{
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
                completed.SetResult(true);
            }
            catch (Exception exception) { completed.SetException(exception); }
            finally
            {
                window?.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
