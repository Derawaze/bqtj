using System.Runtime.InteropServices;

namespace MaaBackgroundProbe;

/// <summary>开发对照输入：消息只送往已验证的Flash子窗口，避开框架的顶层弹窗路由。</summary>
internal static class DirectChildInput
{
    /// <summary>一次有界的移动/按下/释放序列；不激活窗口，不移动系统光标，不自动重试。</summary>
    public static Task ClickAsync(LiveTarget target, Point point, Action assertUndisturbed)
    {
        if (target.ClassName != "MacromediaFlashPlayerActiveX")
            throw new InvalidOperationException("直接子窗口模式仅允许Flash ActiveX目标。");
        return ClickWindowAsync(target.Handle, point, () => LiveTargets.RequireVisibleClient(target), assertUndisturbed);
    }

    /// <summary>共享消息序列；实机入口负责归属检查，合成测试只传入自己创建的子窗口。</summary>
    internal static async Task ClickWindowAsync(IntPtr window, Point point, Func<Size> requireClient, Action assertUndisturbed,
        Action? assertReleaseTarget = null)
    {
        var size = requireClient();
        if (point.X < 0 || point.Y < 0 || point.X >= size.Width || point.Y >= size.Height
            || point.X > short.MaxValue || point.Y > short.MaxValue)
            throw new InvalidOperationException("点击坐标超出有效消息范围。");
        var coordinates = (IntPtr)((point.Y << 16) | point.X);
        await Task.Run(() =>
        {
            void Send(uint message, nuint buttons, bool releasing = false)
            {
                // 已按下后即使取消、最小化也需释放；释放只核对归属，不要求目标可见。
                if (releasing && assertReleaseTarget is not null) assertReleaseTarget();
                else if (requireClient() != size)
                    throw new InvalidOperationException("输入期间客户区尺寸变化，停止操作。");
                if (SendMessageTimeoutW(window, message, buttons, coordinates, 0x23, 1000, out _) == IntPtr.Zero)
                    throw new InvalidOperationException($"子窗口消息0x{message:X}失败或超过1秒，结果不确定；不会重试。");
            }
            assertUndisturbed();
            Send(0x200, 0); // WM_MOUSEMOVE：更新Flash内部悬停位置，不触碰系统光标。
            assertUndisturbed();
            try
            {
                Send(0x201, 1); // WM_LBUTTONDOWN / MK_LBUTTON。
                Thread.Sleep(50);
            }
            finally
            {
                // 按下超时也可能已经送达；仍尝试释放，避免目标保留合成按下状态。
                Send(0x202, 0, releasing: true);
            }
            assertUndisturbed();
        });
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, nuint word, IntPtr value,
        uint flags, uint timeout, out nuint result);
}
