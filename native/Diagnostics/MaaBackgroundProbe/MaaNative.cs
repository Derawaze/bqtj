using System.Runtime.InteropServices;

namespace MaaBackgroundProbe;

/// <summary>独立x64探针与日常工作进程共用的MaaFramework 5.14.2 C接口，不加载进x86面板。</summary>
internal static class MaaNative
{
    private const string Library = "MaaFramework";

    /// <summary>原生依赖仅从已校验的组件目录和系统默认目录加载。</summary>
    public static void Initialize(string directory)
    {
        NativeLibrary.SetDllImportResolver(typeof(MaaNative).Assembly, (name, _, _) =>
        {
            if (name != Library) return IntPtr.Zero;
            var module = LoadLibraryExW(Path.Combine(directory, "MaaFramework.dll"), IntPtr.Zero, 0x1100);
            if (module == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return module;
        });
        // Win32控制库由框架延迟加载，显式预加载使其依赖从组件目录解析。
        if (LoadLibraryExW(Path.Combine(directory, "MaaWin32ControlUnit.dll"), IntPtr.Zero, 0x1100) == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string name, IntPtr file, uint flags);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte MaaGlobalSetOption(int key, byte[] value, ulong size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr MaaWin32ControllerCreate(IntPtr window, ulong screenshot, ulong mouse, ulong keyboard);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void MaaControllerDestroy(IntPtr controller);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte MaaControllerSetOption(IntPtr controller, int key, byte[] value, ulong size);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern long MaaControllerPostConnection(IntPtr controller);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern long MaaControllerPostScreencap(IntPtr controller);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern long MaaControllerPostClick(IntPtr controller, int x, int y);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int MaaControllerStatus(IntPtr controller, long id);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte MaaControllerCachedImage(IntPtr controller, IntPtr image);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr MaaImageBufferCreate();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void MaaImageBufferDestroy(IntPtr image);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr MaaImageBufferGetRawData(IntPtr image);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int MaaImageBufferWidth(IntPtr image);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int MaaImageBufferHeight(IntPtr image);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int MaaImageBufferType(IntPtr image);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr MaaImageBufferGetEncoded(IntPtr image);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong MaaImageBufferGetEncodedSize(IntPtr image);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte MaaImageBufferSetEncoded(IntPtr image, byte[] data, ulong length);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr MaaResourceCreate();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void MaaResourceDestroy(IntPtr resource);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern long MaaResourcePostBundle(IntPtr resource, byte[] path);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int MaaResourceStatus(IntPtr resource, long id);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte MaaResourceOverrideImage(IntPtr resource, byte[] name, IntPtr image);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr MaaTaskerCreate();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void MaaTaskerDestroy(IntPtr tasker);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte MaaTaskerBindResource(IntPtr tasker, IntPtr resource);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern long MaaTaskerPostRecognition(IntPtr tasker, byte[] type, byte[] parameters, IntPtr image);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte MaaTaskerClearCache(IntPtr tasker);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int MaaTaskerStatus(IntPtr tasker, long id);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte MaaTaskerGetTaskDetail(IntPtr tasker, long id, IntPtr entry,
        [Out] long[] nodes, ref ulong size, out int status);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte MaaTaskerGetNodeDetail(IntPtr tasker, long id, IntPtr name,
        out long recognition, out long action, out byte completed);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte MaaTaskerGetRecognitionDetail(IntPtr tasker, long id, IntPtr name, IntPtr algorithm,
        out byte hit, out NativeRect box, IntPtr detail, IntPtr raw, IntPtr draws);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr MaaStringBufferCreate();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void MaaStringBufferDestroy(IntPtr buffer);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr MaaStringBufferGet(IntPtr buffer);

    /// <summary>与官方MaaRect一致的四个32位客户区坐标字段。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect { public int X, Y, Width, Height; }
}
