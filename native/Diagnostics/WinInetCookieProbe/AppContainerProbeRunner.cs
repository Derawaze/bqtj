using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// 按官方 SECURITY_CAPABILITIES 流程创建 AppContainer，并在其中运行合成状态探针。
/// 该诊断 module 不读取 Cookie 内容，只通过子进程退出码判断可见性。
/// </summary>
internal static class AppContainerProbeRunner
{
    private const int ErrorAlreadyExistsHResult = unchecked((int)0x800700B7);
    private const int ErrorInsufficientBuffer = 122;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    // winbase.h: ProcThreadAttributeSecurityCapabilities = 9，并带 Input 标志 0x00020000。
    private const nuint ProcThreadAttributeSecurityCapabilities = 0x00020009;
    private const uint SeGroupEnabled = 0x00000004;
    private const int TokenIsAppContainer = 29;
    private const int TokenCapabilities = 30;

    public static int Run(string profileName, string operation)
    {
        ValidateProfileName(profileName);
        var packageSid = CreateOrOpenProfile(profileName);
        var capabilitySid = DeriveCapabilitySid(
            operation.Equals("fetch-server", StringComparison.OrdinalIgnoreCase)
                ? "internetClientServer"
                : "internetClient");
        try
        {
            var profileFolder = GetProfileFolder(packageSid);
            var executable = CopyProbeIntoProfile(profileFolder);
            return LaunchAndWait(
                executable,
                operation,
                profileFolder,
                packageSid,
                capabilitySid);
        }
        finally
        {
            _ = LocalFree(capabilitySid);
            _ = FreeSid(packageSid);
        }
    }

    /// <summary>
    /// 由 AppContainer 子进程调用，分别验证 token 身份和 internetClient capability。
    /// capability 位于 TokenCapabilities，而非普通 token group，不能用 CheckTokenMembership 判断。
    /// </summary>
    public static int CheckExpectedIdentityAndInternetCapability()
    {
        using var process = Process.GetCurrentProcess();
        if (!OpenProcessToken(process.Handle, 0x0008, out var token))
        {
            return 20;
        }

        try
        {
            var buffer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                if (!GetTokenInformation(
                        token,
                        TokenIsAppContainer,
                        buffer,
                        sizeof(int),
                        out _))
                {
                    return 21;
                }

                if (Marshal.ReadInt32(buffer) == 0)
                {
                    return 22;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            var capabilitySid = DeriveCapabilitySid("internetClient");
            try
            {
                return FindTokenCapability(token, capabilitySid);
            }
            finally
            {
                _ = LocalFree(capabilitySid);
            }
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    /// <summary>枚举 TokenCapabilities，0 表示存在 internetClient，24/25 分别表示读取失败或未找到。</summary>
    private static int FindTokenCapability(nint token, nint expectedSid)
    {
        _ = GetTokenInformation(token, TokenCapabilities, nint.Zero, 0, out var bufferSize);
        if (bufferSize <= 0 || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
        {
            return 24;
        }

        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (!GetTokenInformation(
                    token,
                    TokenCapabilities,
                    buffer,
                    bufferSize,
                    out _))
            {
                return 24;
            }

            var count = checked((uint)Marshal.ReadInt32(buffer));
            var firstGroupOffset = Marshal.OffsetOf<TokenGroupsHeader>(
                nameof(TokenGroupsHeader.FirstGroup)).ToInt32();
            var groupSize = Marshal.SizeOf<SidAndAttributes>();
            for (var index = 0u; index < count; index++)
            {
                var groupPointer = nint.Add(
                    buffer,
                    checked(firstGroupOffset + (int)index * groupSize));
                var group = Marshal.PtrToStructure<SidAndAttributes>(groupPointer);
                if (EqualSid(group.Sid, expectedSid))
                {
                    return 0;
                }
            }

            return 25;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static nint CreateOrOpenProfile(string profileName)
    {
        var result = CreateAppContainerProfile(
            profileName,
            profileName,
            "爆枪突击账号隔离诊断",
            nint.Zero,
            0,
            out var packageSid);
        if (result == ErrorAlreadyExistsHResult)
        {
            result = DeriveAppContainerSidFromAppContainerName(profileName, out packageSid);
        }

        Marshal.ThrowExceptionForHR(result);
        return packageSid;
    }

    private static nint DeriveCapabilitySid(string capabilityName)
    {
        if (!DeriveCapabilitySidsFromName(
                capabilityName,
                out var groupSids,
                out var groupCount,
                out var capabilitySids,
                out var capabilityCount)
            || capabilityCount != 1)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"无法派生 {capabilityName} capability SID。");
        }

        try
        {
            if (groupCount > 0)
            {
                _ = LocalFree(Marshal.ReadIntPtr(groupSids));
            }

            return Marshal.ReadIntPtr(capabilitySids);
        }
        finally
        {
            _ = LocalFree(groupSids);
            _ = LocalFree(capabilitySids);
        }
    }

    private static string GetProfileFolder(nint packageSid)
    {
        if (!ConvertSidToStringSid(packageSid, out var sidTextPointer))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var sidText = Marshal.PtrToStringUni(sidTextPointer)
                ?? throw new InvalidOperationException("无法读取 AppContainer SID。");
            Marshal.ThrowExceptionForHR(GetAppContainerFolderPath(sidText, out var pathPointer));
            try
            {
                return Marshal.PtrToStringUni(pathPointer)
                    ?? throw new InvalidOperationException("无法读取 AppContainer profile 路径。");
            }
            finally
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }
        finally
        {
            _ = LocalFree(sidTextPointer);
        }
    }

    private static string CopyProbeIntoProfile(string profileFolder)
    {
        var destination = Path.Combine(profileFolder, "BqtjIsolationProbe");
        Directory.CreateDirectory(destination);
        foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory))
        {
            var extension = Path.GetExtension(source);
            if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(source, Path.Combine(destination, Path.GetFileName(source)), overwrite: true);
            }
        }

        return Path.Combine(destination, "WinInetCookieProbe.exe");
    }

    private static int LaunchAndWait(
        string executable,
        string operation,
        string workingDirectory,
        nint packageSid,
        nint capabilitySid)
    {
        nuint attributeSize = 0;
        _ = InitializeProcThreadAttributeList(nint.Zero, 1, 0, ref attributeSize);
        if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var attributeList = Marshal.AllocHGlobal(checked((int)attributeSize));
        var capabilityArray = Marshal.AllocHGlobal(Marshal.SizeOf<SidAndAttributes>());
        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            Marshal.StructureToPtr(
                new SidAndAttributes { Sid = capabilitySid, Attributes = SeGroupEnabled },
                capabilityArray,
                fDeleteOld: false);
            var securityCapabilities = new SecurityCapabilities
            {
                AppContainerSid = packageSid,
                Capabilities = capabilityArray,
                CapabilityCount = 1,
            };
            var securityCapabilitiesPointer = Marshal.AllocHGlobal(
                Marshal.SizeOf<SecurityCapabilities>());
            try
            {
                Marshal.StructureToPtr(
                    securityCapabilities,
                    securityCapabilitiesPointer,
                    fDeleteOld: false);
                if (!UpdateProcThreadAttribute(
                        attributeList,
                        0,
                        ProcThreadAttributeSecurityCapabilities,
                        securityCapabilitiesPointer,
                        (nuint)Marshal.SizeOf<SecurityCapabilities>(),
                        nint.Zero,
                        nint.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var startupInfo = new StartupInfoEx
                {
                    StartupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>() },
                    AttributeList = attributeList,
                };
                var commandLine = new StringBuilder($"\"{executable}\" {operation}");
                if (!CreateProcess(
                        null,
                        commandLine,
                        nint.Zero,
                        nint.Zero,
                        false,
                        ExtendedStartupInfoPresent,
                        nint.Zero,
                        workingDirectory,
                        ref startupInfo,
                        out var processInformation))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                try
                {
                    _ = WaitForSingleObject(processInformation.Process, 60_000);
                    if (!GetExitCodeProcess(processInformation.Process, out var exitCode))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    return unchecked((int)exitCode);
                }
                finally
                {
                    _ = CloseHandle(processInformation.Thread);
                    _ = CloseHandle(processInformation.Process);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(securityCapabilitiesPointer);
            }
        }
        finally
        {
            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(capabilityArray);
            Marshal.FreeHGlobal(attributeList);
        }
    }

    private static void ValidateProfileName(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)
            || profileName.Length > 64
            || profileName.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            throw new ArgumentException("AppContainer profile 名称无效。", nameof(profileName));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenGroupsHeader
    {
        public uint GroupCount;
        public SidAndAttributes FirstGroup;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityCapabilities
    {
        public nint AppContainerSid;
        public nint Capabilities;
        public uint CapabilityCount;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public nint Reserved2Pointer;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int CreateAppContainerProfile(
        string appContainerName,
        string displayName,
        string description,
        nint capabilities,
        uint capabilityCount,
        out nint appContainerSid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeriveAppContainerSidFromAppContainerName(
        string appContainerName,
        out nint appContainerSid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int GetAppContainerFolderPath(string appContainerSid, out nint path);

    [DllImport("kernelbase.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeriveCapabilitySidsFromName(
        string capabilityName,
        out nint capabilityGroupSids,
        out uint capabilityGroupSidCount,
        out nint capabilitySids,
        out uint capabilitySidCount);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSidToStringSid(nint sid, out nint stringSid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern nint FreeSid(nint sid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint LocalFree(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        nint attributeList,
        int attributeCount,
        int flags,
        ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        nint attributeList,
        uint flags,
        nuint attribute,
        nint value,
        nuint size,
        nint previousValue,
        nint returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint attributeList);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [SuppressMessage(
        "Performance",
        "CA1838:Avoid StringBuilder parameters for P/Invokes",
        Justification = "CreateProcessW 按 Win32 约定可能修改命令行缓冲区，必须提供可写内存。")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string? applicationName,
        StringBuilder commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(nint process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        nint token,
        int tokenInformationClass,
        nint tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EqualSid(nint firstSid, nint secondSid);
}
