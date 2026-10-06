using System.Runtime.InteropServices;
using BqtjLauncher.Application;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>仅遍历自身承载面板的后代，且要求属于本原生子进程；不扫描桌面或按标题绑定。</summary>
internal static class AutomationTargetScanner
{
    internal static AutomationWindowTarget Read(Guid sessionId, Guid profileId, int nativePid, nint surface)
    {
        // EnumChildWindows的空父句柄会退化成桌面枚举，必须在入口拒绝空值或外进程面板。
        if (surface == 0 || GetWindowThreadProcessId(surface, out var surfacePid) == 0 || surfacePid != Environment.ProcessId)
            throw new InvalidOperationException("自动化承载面板不属于本容器。");
        var matches = new List<nint>();
        EnumChildWindows(surface, (child, _) =>
        {
            var name = new char[128];
            var count = GetClassNameW(child, name, name.Length);
            if (count > 0 && new string(name, 0, count) == "MacromediaFlashPlayerActiveX"
                && GetWindowThreadProcessId(child, out var pid) != 0 && pid == nativePid)
                matches.Add(child);
            return true;
        }, 0);
        if (matches.Count != 1 || !GetClientRect(matches[0], out var size) || size.Right <= 0 || size.Bottom <= 0)
            throw new InvalidOperationException("本会话尚无唯一有效Flash窗口。");
        var handle = IntPtr.Size == 4 ? unchecked((uint)matches[0].ToInt32()) : matches[0].ToInt64();
        return new(sessionId, profileId, Environment.ProcessId, nativePid, handle, size.Right, size.Bottom);
    }

    private delegate bool WindowCallback(nint window, nint parameter);
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint parent, WindowCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint window, [Out] char[] name, int count);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint window, out Rect rectangle);
}
