using System.Windows;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>统一首次透明显示游戏容器；实际客户区校准完成后由容器显示加载层。</summary>
public static class GameWindowStartupDisplay
{
    /// <summary>按已配置档位首次显示；普通窗口暂不激活，全屏允许激活以满足 WPF 约束。</summary>
    public static void ShowLoading(Window window)
    {
        window.Opacity = 0;
        // WPF 禁止 ShowActivated=false 与 Maximized 一起首次显示；保留全屏状态，不先还原再放大。
        window.ShowActivated = window.WindowState == WindowState.Maximized;
        window.Show();
    }
}
