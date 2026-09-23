using System.Runtime.InteropServices;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>
/// 持续把静音偏好应用到指定游戏进程的 Windows 音频会话。
/// Flash 会延迟创建或在刷新后重建音频会话，因此静音时需要周期性校准。
/// </summary>
internal sealed class GameAudioSessionMute : IDisposable
{
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _monitorTask;
    private int _processId;
    private bool _isMuted;
    private bool _disposed;

    public void Attach(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(processId, 1);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_stateGate)
        {
            if (_processId != 0)
            {
                throw new InvalidOperationException("游戏音频控制器已经绑定进程。");
            }

            _processId = processId;
            _monitorTask = Task.Run(MonitorAsync);
        }
    }

    public void SetMuted(bool isMuted)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int processId;
        lock (_stateGate)
        {
            _isMuted = isMuted;
            processId = _processId;
        }

        if (processId != 0)
        {
            TryApply(processId, isMuted);
        }
    }

    private async Task MonitorAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                int processId;
                bool isMuted;
                lock (_stateGate)
                {
                    processId = _processId;
                    isMuted = _isMuted;
                }

                // 未静音时无需轮询；取消静音已由 SetMuted 立即应用到现有会话。
                if (isMuted && processId != 0)
                {
                    TryApply(processId, isMuted: true);
                }

                await Task.Delay(TimeSpan.FromSeconds(1), _shutdown.Token);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private static void TryApply(int processId, bool isMuted)
    {
        try
        {
            CoreAudioSessionAdapter.SetProcessMuted(processId, isMuted);
        }
        catch (COMException)
        {
            // 音频设备或会话可能尚未创建；静音状态会在下一轮自动重试。
        }
        catch (InvalidCastException)
        {
            // 某些非标准音频会话不提供所需 interface，跳过即可。
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        try
        {
            _monitorTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        _shutdown.Dispose();
    }
}

/// <summary>通过 Windows Core Audio 枚举并设置单个进程的音频会话。</summary>
internal static class CoreAudioSessionAdapter
{
    private static readonly Guid EventContext = new("32B0DEB7-E36C-4DF7-8D5E-1A872F97156D");

    public static void SetProcessMuted(int processId, bool isMuted)
    {
        IMMDeviceEnumerator? deviceEnumerator = null;
        IMMDevice? device = null;
        IAudioSessionManager2? sessionManager = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            object deviceEnumeratorObject = new MMDeviceEnumerator();
            deviceEnumerator = (IMMDeviceEnumerator)deviceEnumeratorObject;
            Marshal.ThrowExceptionForHR(deviceEnumerator.GetDefaultAudioEndpoint(
                EDataFlow.Render,
                ERole.Multimedia,
                out device));

            var interfaceId = typeof(IAudioSessionManager2).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(
                ref interfaceId,
                ClsContext.All,
                nint.Zero,
                out var managerObject));
            sessionManager = (IAudioSessionManager2)managerObject;
            Marshal.ThrowExceptionForHR(sessionManager.GetSessionEnumerator(out sessions));
            Marshal.ThrowExceptionForHR(sessions.GetCount(out var count));

            for (var index = 0; index < count; index++)
            {
                IAudioSessionControl? session = null;
                try
                {
                    Marshal.ThrowExceptionForHR(sessions.GetSession(index, out session));
                    if (session is not IAudioSessionControl2 session2)
                    {
                        continue;
                    }

                    Marshal.ThrowExceptionForHR(session2.GetProcessId(out var sessionProcessId));
                    if (sessionProcessId != processId || session is not ISimpleAudioVolume volume)
                    {
                        continue;
                    }

                    var context = EventContext;
                    Marshal.ThrowExceptionForHR(volume.SetMute(isMuted, ref context));
                }
                finally
                {
                    Release(session);
                }
            }
        }
        finally
        {
            Release(sessions);
            Release(sessionManager);
            Release(device);
            Release(deviceEnumerator);
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.ReleaseComObject(value);
        }
    }

    private enum EDataFlow
    {
        Render,
        Capture,
        All,
    }

    private enum ERole
    {
        Console,
        Multimedia,
        Communications,
    }

    [Flags]
    private enum ClsContext : uint
    {
        InProcessServer = 0x1,
        InProcessHandler = 0x2,
        LocalServer = 0x4,
        RemoteServer = 0x10,
        All = InProcessServer | InProcessHandler | LocalServer | RemoteServer,
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumerator;

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out nint devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(nint callback);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(nint callback);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid interfaceId,
            ClsContext context,
            nint activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig]
        int GetAudioSessionControl(ref Guid sessionGuid, uint streamFlags, out nint sessionControl);

        [PreserveSig]
        int GetSimpleAudioVolume(ref Guid sessionGuid, uint streamFlags, out nint audioVolume);

        [PreserveSig]
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);

        [PreserveSig]
        int RegisterSessionNotification(nint notification);

        [PreserveSig]
        int UnregisterSessionNotification(nint notification);

        [PreserveSig]
        int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, nint notification);

        [PreserveSig]
        int UnregisterDuckNotification(nint notification);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig]
        int GetCount(out int sessionCount);

        [PreserveSig]
        int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig]
        int GetState(out int state);

        [PreserveSig]
        int GetDisplayName(out nint displayName);

        [PreserveSig]
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

        [PreserveSig]
        int GetIconPath(out nint iconPath);

        [PreserveSig]
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

        [PreserveSig]
        int GetGroupingParam(out Guid groupingId);

        [PreserveSig]
        int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

        [PreserveSig]
        int RegisterAudioSessionNotification(nint notification);

        [PreserveSig]
        int UnregisterAudioSessionNotification(nint notification);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig]
        int GetState(out int state);

        [PreserveSig]
        int GetDisplayName(out nint displayName);

        [PreserveSig]
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

        [PreserveSig]
        int GetIconPath(out nint iconPath);

        [PreserveSig]
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

        [PreserveSig]
        int GetGroupingParam(out Guid groupingId);

        [PreserveSig]
        int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

        [PreserveSig]
        int RegisterAudioSessionNotification(nint notification);

        [PreserveSig]
        int UnregisterAudioSessionNotification(nint notification);

        [PreserveSig]
        int GetSessionIdentifier(out nint sessionIdentifier);

        [PreserveSig]
        int GetSessionInstanceIdentifier(out nint sessionInstanceIdentifier);

        [PreserveSig]
        int GetProcessId(out int processId);

        [PreserveSig]
        int IsSystemSoundsSession();

        [PreserveSig]
        int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig]
        int SetMasterVolume(float level, ref Guid eventContext);

        [PreserveSig]
        int GetMasterVolume(out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool isMuted, ref Guid eventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool isMuted);
    }
}
