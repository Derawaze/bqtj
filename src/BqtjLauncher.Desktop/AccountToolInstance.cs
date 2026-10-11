using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BqtjLauncher.Desktop;

/// <summary>当前 Windows 会话的账号工具单实例；命名事件只通知激活，不传递凭据。</summary>
internal sealed class AccountToolInstance : IDisposable
{
    internal const string DefaultName = @"Local\BqtjLauncher.AccountManager";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private RegisteredWaitHandle? _wait;
    private bool _disposed;

    private AccountToolInstance(Mutex mutex, EventWaitHandle activation)
    { _mutex = mutex; _activation = activation; }

    /// <summary>事件先于初始化创建，快速连点的第二个入口也能通知尚未显示的首个窗口。</summary>
    internal static AccountToolInstance? TryCreate(string name = DefaultName)
    {
        var activation = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Activate");
        var mutex = new Mutex(false, name + ".Owner");
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (acquired) return new(mutex, activation);
        GrantForeground();
        activation.Set();
        activation.Dispose();
        mutex.Dispose();
        return null;
    }

    /// <summary>所有面板入口先通知原窗口，避免为了激活而再启动一份运行库。</summary>
    internal static bool TryActivate(string name = DefaultName)
    {
        EventWaitHandle activation;
        try { activation = EventWaitHandle.OpenExisting(name + ".Activate"); }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        using (activation) { GrantForeground(); activation.Set(); }
        return true;
    }

    internal void Listen(Window window, Action activated)
    {
        _wait = ThreadPool.RegisterWaitForSingleObject(_activation, (_, _) =>
        {
            if (_disposed) return;
            window.Dispatcher.BeginInvoke(() =>
            {
                if (_disposed || !window.IsVisible) return;
                var handle = new WindowInteropHelper(window).Handle;
                // SW_RESTORE 保留最小化前的普通或最大化状态。
                if (window.WindowState == WindowState.Minimized) ShowWindow(handle, 9);
                window.Activate();
                // 重复审核是模态窗口时，将焦点交给原窗口的活动弹窗。
                var popup = GetLastActivePopup(handle);
                SetForegroundWindow(IsWindowVisible(popup) ? popup : handle);
                activated();
            });
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private static void GrantForeground()
    {
        var handle = FindWindow(null, "账号管理工具");
        if (handle != IntPtr.Zero)
        {
            if (GetWindowThreadProcessId(handle, out var processId) != 0) AllowSetForegroundWindow(processId);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wait?.Unregister(null);
        _activation.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string title);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll")]
    private static extern IntPtr GetLastActivePopup(IntPtr handle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr handle, int command);
}
