using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MaaBackgroundProbe;

/// <summary>只发现启动器原生宿主内部的窗口，不读取窗口标题、账号或进程命令行。</summary>
internal sealed record LiveTarget(IntPtr Handle, uint ProcessId, string ClassName)
{
    public override string ToString() => $"PID {ProcessId} / {ClassName} / 0x{Handle:X}";
}

internal static class LiveTargets
{
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);

    public static IReadOnlyList<LiveTarget> Find()
    {
        var candidates = new Dictionary<IntPtr, LiveTarget>();
        void Visit(IntPtr window)
        {
            var name = ReadClass(window);
            if (name is not ("BqtjNativeFlashHost" or "Internet Explorer_Server" or "MacromediaFlashPlayerActiveX")) return;
            if (GetWindowThreadProcessId(window, out var pid) == 0) return;
            var target = new LiveTarget(window, pid, name);
            if (IsValid(target)) candidates[window] = target;
        }
        EnumWindows((window, _) =>
        {
            Visit(window);
            EnumChildWindows(window, (child, _) => { Visit(child); return true; }, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        return candidates.Values.OrderBy(t => t.ProcessId).ThenBy(t => t.ClassName, StringComparer.Ordinal).ToArray();
    }

    /// <summary>每次操作重新检查句柄、进程和宿主祖先，刷新销毁的窗口不能继续使用。</summary>
    public static bool IsValid(LiveTarget target)
    {
        if (!IsWindow(target.Handle) || ReadClass(target.Handle) != target.ClassName) return false;
        if (GetWindowThreadProcessId(target.Handle, out var pid) == 0) return false;
        if (pid != target.ProcessId) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (process.ProcessName != "BqtjNativeFlashHost") return false;
        }
        catch (ArgumentException) { return false; }
        for (var parent = target.Handle; parent != IntPtr.Zero; parent = GetParent(parent))
        {
            if (ReadClass(parent) == "BqtjNativeFlashHost") return true;
        }
        return false;
    }

    public static Size RequireVisibleClient(LiveTarget target)
    {
        if (!IsValid(target) || !IsWindowVisible(target.Handle) || IsIconic(GetAncestor(target.Handle, 2)))
            throw new InvalidOperationException("目标已关闭、隐藏或最小化，请重新选择。不会自动激活或恢复窗口。");
        if (!GetClientRect(target.Handle, out var rectangle) || rectangle.Right <= 0 || rectangle.Bottom <= 0)
            throw new InvalidOperationException("无法读取目标客户区尺寸。");
        return new Size(rectangle.Right, rectangle.Bottom);
    }

    private static string ReadClass(IntPtr window)
    {
        var text = new char[256];
        var length = GetClassNameW(window, text, text.Length);
        return length > 0 ? new string(text, 0, length) : "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, [Out] char[] text, int count);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out Rect rectangle);
}
