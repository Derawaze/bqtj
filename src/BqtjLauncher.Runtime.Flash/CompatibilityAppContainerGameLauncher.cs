using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>
/// AppContainer 兼容性验证入口。该 module 已实现 fail-closed 启动，但尚未接入正式 broker、作业对象和删除生命周期。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CompatibilityAppContainerGameLauncher
{
    /// <summary>
    /// 将指定 self-contained 发布目录暂存到普通用户只读执行缓存，然后在账号专属 AppContainer 中启动。
    /// 子进程先以挂起状态创建；只有 token 身份和 package SID 验证通过后才恢复执行。
    /// </summary>
    public static IsolatedGameLaunchResult Start(CompatibilityAppContainerLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        var moniker = AccountIsolationIdentity.CreateMoniker(request.AccountId);
        using var profile = AppContainerProfile.CreateOrOpen(moniker);
        var stagedPayload = IsolatedPayloadStore.Stage(
            request.PayloadSourceDirectory,
            profile.PackageSidText);
        var executablePath = Path.Combine(
            stagedPayload.DirectoryPath,
            IsolatedPayloadManifest.EntryPointFileName);

        var process = AppContainerProcessLauncher.StartSuspendedAndVerify(
            executablePath,
            request.Arguments,
            stagedPayload.DirectoryPath,
            profile,
            request.InheritedHandles);
        return new IsolatedGameLaunchResult(
            process,
            moniker,
            profile.PackageSidText,
            stagedPayload.DirectoryPath,
            stagedPayload.ManifestHash);
    }
}

/// <summary>描述 compatibility spike 的启动输入；正式运行链接入前仍需 broker 与会话作业对象。</summary>
public sealed record CompatibilityAppContainerLaunchRequest(
    Guid AccountId,
    string PayloadSourceDirectory,
    IReadOnlyList<string> Arguments,
    AppContainerInheritedHandles? InheritedHandles = null)
{
    internal void Validate()
    {
        _ = AccountIsolationIdentity.CreateMoniker(AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(PayloadSourceDirectory);
        ArgumentNullException.ThrowIfNull(Arguments);
        if (!Path.IsPathFullyQualified(PayloadSourceDirectory))
        {
            throw new ArgumentException("payload 源目录必须是绝对路径。", nameof(PayloadSourceDirectory));
        }

        if (Arguments.Any(argument => argument is null))
        {
            throw new ArgumentException("启动参数不能包含 null。", nameof(Arguments));
        }

        InheritedHandles?.Validate();
    }
}

/// <summary>返回已验证身份且已恢复执行的容器进程及其可审计隔离信息。</summary>
public sealed record IsolatedGameLaunchResult(
    Process Process,
    string Moniker,
    string PackageSid,
    string PayloadDirectory,
    string ManifestHash);

/// <summary>
/// 为后续 broker 的匿名管道预留显式继承句柄。标准输入、输出、错误必须同时提供或同时省略。
/// </summary>
public sealed record AppContainerInheritedHandles(
    SafeHandle? StandardInput,
    SafeHandle? StandardOutput,
    SafeHandle? StandardError,
    IReadOnlyList<SafeHandle>? AdditionalHandles = null)
{
    internal void Validate()
    {
        var standardHandleCount = new[] { StandardInput, StandardOutput, StandardError }
            .Count(handle => handle is not null);
        if (standardHandleCount is not (0 or 3))
        {
            throw new ArgumentException("标准输入、输出和错误句柄必须同时提供或同时省略。");
        }

        foreach (var handle in EnumerateAll())
        {
            if (handle.IsClosed || handle.IsInvalid)
            {
                throw new ArgumentException("继承句柄已关闭或无效。");
            }
        }
    }

    internal IEnumerable<SafeHandle> EnumerateAll()
    {
        if (StandardInput is not null)
        {
            yield return StandardInput;
            yield return StandardOutput!;
            yield return StandardError!;
        }

        if (AdditionalHandles is not null)
        {
            foreach (var handle in AdditionalHandles)
            {
                yield return handle;
            }
        }
    }
}

/// <summary>在普通用户缓存内维护以 manifest SHA-256 命名的不可变 payload。</summary>
[SupportedOSPlatform("windows")]
internal static class IsolatedPayloadStore
{
    private const string CacheDirectoryName = "BqtjLauncherRuntime";

    public static StagedIsolatedPayload Stage(string sourceDirectory, string packageSidText)
    {
        var manifest = IsolatedPayloadManifest.Create(sourceDirectory);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("无法确定当前用户的 LocalApplicationData 目录。");
        }

        var cacheRoot = Path.Combine(localAppData, CacheDirectoryName);
        var payloadRoot = Path.Combine(cacheRoot, "payloads");
        EnsureDirectoryAccess(cacheRoot, packageSidText);
        EnsureDirectoryAccess(payloadRoot, packageSidText);

        var target = Path.Combine(payloadRoot, manifest.ManifestHash);
        if (Directory.Exists(target))
        {
            EnsureDirectoryAccess(target, packageSidText);
            VerifyPayload(target, manifest);
            return new StagedIsolatedPayload(target, manifest.ManifestHash);
        }

        var temporary = Path.Combine(
            payloadRoot,
            $".{manifest.ManifestHash}.tmp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            EnsureDirectoryAccess(temporary, packageSidText);
            CopyAndVerifyFiles(sourceDirectory, temporary, manifest);
            File.WriteAllText(
                Path.Combine(temporary, IsolatedPayloadManifest.MarkerFileName),
                manifest.CanonicalText,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            try
            {
                Directory.Move(temporary, target);
            }
            catch (IOException) when (Directory.Exists(target))
            {
                // 另一个会话已原子发布相同内容时，只接受通过完整复验的既有目录。
                VerifyPayload(target, manifest);
            }

            EnsureDirectoryAccess(target, packageSidText);
            VerifyPayload(target, manifest);
            return new StagedIsolatedPayload(target, manifest.ManifestHash);
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
        }
    }

    private static void CopyAndVerifyFiles(
        string sourceDirectory,
        string destinationDirectory,
        IsolatedPayloadManifest manifest)
    {
        var sourceRoot = Path.GetFullPath(sourceDirectory);
        foreach (var entry in manifest.Files)
        {
            var relativePath = entry.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            var source = Path.Combine(sourceRoot, relativePath);
            var destination = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: false);
        }

        VerifyPayloadFiles(destinationDirectory, manifest);
    }

    private static void VerifyPayload(string directory, IsolatedPayloadManifest manifest)
    {
        var marker = Path.Combine(directory, IsolatedPayloadManifest.MarkerFileName);
        if (!File.Exists(marker)
            || !string.Equals(File.ReadAllText(marker), manifest.CanonicalText, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"隔离 payload manifest 不匹配：{directory}");
        }

        VerifyPayloadFiles(directory, manifest);
    }

    private static void VerifyPayloadFiles(string directory, IsolatedPayloadManifest manifest)
    {
        foreach (var entry in manifest.Files)
        {
            var path = Path.Combine(
                directory,
                entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.SequentialScan);
            var actualHash = Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(stream));
            if (stream.Length != entry.Length
                || !actualHash.Equals(entry.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"隔离 payload 文件校验失败：{entry.RelativePath}");
            }
        }
    }

    private static void EnsureDirectoryAccess(string path, string packageSidText)
    {
        var directory = Directory.CreateDirectory(path);
        var security = directory.GetAccessControl(AccessControlSections.Access);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var propagation = PropagationFlags.None;
        var fullControl = FileSystemRights.FullControl;
        var readExecute = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;

        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("无法读取当前 Windows 用户 SID。");
        AddAllowRule(security, currentUser, fullControl, inheritance, propagation);
        AddAllowRule(
            security,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            fullControl,
            inheritance,
            propagation);
        AddAllowRule(
            security,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            fullControl,
            inheritance,
            propagation);
        AddAllowRule(
            security,
            new SecurityIdentifier(packageSidText),
            readExecute,
            inheritance,
            propagation);
        directory.SetAccessControl(security);
    }

    private static void AddAllowRule(
        DirectorySecurity security,
        SecurityIdentifier identity,
        FileSystemRights rights,
        InheritanceFlags inheritance,
        PropagationFlags propagation) =>
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            rights,
            inheritance,
            propagation,
            AccessControlType.Allow));
}

internal sealed record StagedIsolatedPayload(string DirectoryPath, string ManifestHash);

/// <summary>负责创建或派生账号 profile，并管理 Win32 返回的 SID 生命周期。</summary>
[SupportedOSPlatform("windows")]
internal sealed class AppContainerProfile : IDisposable
{
    private const int ErrorAlreadyExistsHResult = unchecked((int)0x800700B7);
    private nint _packageSid;

    private AppContainerProfile(string moniker, nint packageSid, string packageSidText)
    {
        Moniker = moniker;
        _packageSid = packageSid;
        PackageSidText = packageSidText;
    }

    public string Moniker { get; }

    public nint PackageSid => _packageSid != nint.Zero
        ? _packageSid
        : throw new ObjectDisposedException(nameof(AppContainerProfile));

    public string PackageSidText { get; }

    public static AppContainerProfile CreateOrOpen(string moniker)
    {
        var capabilitySid = DeriveInternetClientCapability();
        var capability = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.SidAndAttributes>());
        int result;
        nint packageSid;
        try
        {
            // profile 创建阶段也必须声明网络能力，Windows 网络隔离策略才会登记该身份。
            Marshal.StructureToPtr(
                new NativeMethods.SidAndAttributes
                {
                    Sid = capabilitySid,
                    Attributes = 0x00000004,
                },
                capability,
                fDeleteOld: false);
            result = NativeMethods.CreateAppContainerProfile(
                moniker,
                moniker,
                "爆枪突击账号隔离容器",
                capability,
                1,
                out packageSid);
        }
        finally
        {
            Marshal.FreeHGlobal(capability);
            _ = NativeMethods.LocalFree(capabilitySid);
        }

        if (result == ErrorAlreadyExistsHResult)
        {
            result = NativeMethods.DeriveAppContainerSidFromAppContainerName(
                moniker,
                out packageSid);
        }

        Marshal.ThrowExceptionForHR(result);
        if (!NativeMethods.ConvertSidToStringSid(packageSid, out var sidTextPointer))
        {
            _ = NativeMethods.FreeSid(packageSid);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 AppContainer package SID。");
        }

        try
        {
            var sidText = Marshal.PtrToStringUni(sidTextPointer)
                ?? throw new InvalidOperationException("AppContainer package SID 为空。");
            return new AppContainerProfile(moniker, packageSid, sidText);
        }
        catch
        {
            _ = NativeMethods.FreeSid(packageSid);
            throw;
        }
        finally
        {
            _ = NativeMethods.LocalFree(sidTextPointer);
        }
    }

    private static nint DeriveInternetClientCapability()
    {
        if (!NativeMethods.DeriveCapabilitySidsFromName(
                "internetClient",
                out var groupSids,
                out var groupCount,
                out var capabilitySids,
                out var capabilityCount)
            || capabilityCount != 1)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "无法为 AppContainer profile 派生 internetClient capability SID。");
        }

        try
        {
            for (var index = 0u; index < groupCount; index++)
            {
                _ = NativeMethods.LocalFree(
                    Marshal.ReadIntPtr(groupSids, checked((int)index * nint.Size)));
            }

            return Marshal.ReadIntPtr(capabilitySids);
        }
        finally
        {
            _ = NativeMethods.LocalFree(groupSids);
            _ = NativeMethods.LocalFree(capabilitySids);
        }
    }

    public void Dispose()
    {
        if (_packageSid != nint.Zero)
        {
            _ = NativeMethods.FreeSid(_packageSid);
            _packageSid = nint.Zero;
        }
    }
}

/// <summary>通过 SECURITY_CAPABILITIES 创建并验证低权限进程，绝不回退为普通 Process.Start。</summary>
[SupportedOSPlatform("windows")]
internal static class AppContainerProcessLauncher
{
    private const int ErrorInsufficientBuffer = 122;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateSuspended = 0x00000004;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint SeGroupEnabled = 0x00000004;
    private const nuint ProcThreadAttributeSecurityCapabilities = 0x00020009;
    private const nuint ProcThreadAttributeHandleList = 0x00020002;
    private const int TokenIsAppContainer = 29;
    private const int TokenAppContainerSid = 31;
    private const uint TokenQuery = 0x0008;

    public static Process StartSuspendedAndVerify(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        AppContainerProfile profile,
        AppContainerInheritedHandles? inheritedHandles)
    {
        using var capability = InternetClientCapability.Create();
        using var handleLease = InheritedHandleLease.Create(inheritedHandles);
        using var attributes = StartupAttributeList.Create(
            profile.PackageSid,
            capability.Sid,
            handleLease.NativeHandles);

        var startupInfo = new NativeMethods.StartupInfoEx
        {
            StartupInfo = new NativeMethods.StartupInfo
            {
                Size = Marshal.SizeOf<NativeMethods.StartupInfoEx>(),
            },
            AttributeList = attributes.Pointer,
        };
        if (inheritedHandles?.StandardInput is not null)
        {
            startupInfo.StartupInfo.Flags = StartfUseStdHandles;
            startupInfo.StartupInfo.StandardInput = inheritedHandles.StandardInput.DangerousGetHandle();
            startupInfo.StartupInfo.StandardOutput = inheritedHandles.StandardOutput!.DangerousGetHandle();
            startupInfo.StartupInfo.StandardError = inheritedHandles.StandardError!.DangerousGetHandle();
        }

        var commandLine = new StringBuilder(WindowsCommandLine.Build(executablePath, arguments));
        if (!NativeMethods.CreateProcess(
                executablePath,
                commandLine,
                nint.Zero,
                nint.Zero,
                handleLease.NativeHandles.Length > 0,
                ExtendedStartupInfoPresent | CreateSuspended,
                nint.Zero,
                workingDirectory,
                ref startupInfo,
                out var processInformation))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 AppContainer 游戏容器。");
        }

        Process? process = null;
        var resumed = false;
        try
        {
            VerifyToken(processInformation.Process, profile.PackageSid);
            process = Process.GetProcessById(checked((int)processInformation.ProcessId));
            if (NativeMethods.ResumeThread(processInformation.Thread) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法恢复 AppContainer 游戏容器线程。");
            }

            resumed = true;
            return process;
        }
        catch
        {
            if (!resumed)
            {
                _ = NativeMethods.TerminateProcess(processInformation.Process, 0xC0000022);
            }

            process?.Dispose();
            throw;
        }
        finally
        {
            _ = NativeMethods.CloseHandle(processInformation.Thread);
            _ = NativeMethods.CloseHandle(processInformation.Process);
        }
    }

    private static void VerifyToken(nint processHandle, nint expectedPackageSid)
    {
        if (!NativeMethods.OpenProcessToken(processHandle, TokenQuery, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 AppContainer 进程 token。");
        }

        try
        {
            var appContainerFlag = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                if (!NativeMethods.GetTokenInformation(
                        token,
                        TokenIsAppContainer,
                        appContainerFlag,
                        sizeof(int),
                        out _)
                    || Marshal.ReadInt32(appContainerFlag) == 0)
                {
                    throw new InvalidOperationException("子进程 token 不是 AppContainer；已拒绝继续运行。");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(appContainerFlag);
            }

            _ = NativeMethods.GetTokenInformation(
                token,
                TokenAppContainerSid,
                nint.Zero,
                0,
                out var informationSize);
            if (informationSize < Marshal.SizeOf<NativeMethods.TokenAppContainerInformation>())
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "无法确定子进程 package SID 缓冲区大小。");
            }

            var information = Marshal.AllocHGlobal(informationSize);
            try
            {
                if (!NativeMethods.GetTokenInformation(
                        token,
                        TokenAppContainerSid,
                        information,
                        informationSize,
                        out _))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取子进程 package SID。");
                }

                var actual = Marshal.PtrToStructure<NativeMethods.TokenAppContainerInformation>(information);
                if (actual.TokenAppContainer == nint.Zero
                    || !NativeMethods.EqualSid(actual.TokenAppContainer, expectedPackageSid))
                {
                    throw new InvalidOperationException("子进程 package SID 与目标账号不一致；已拒绝继续运行。");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(information);
            }
        }
        finally
        {
            _ = NativeMethods.CloseHandle(token);
        }
    }

    private sealed class InternetClientCapability : IDisposable
    {
        private nint _sid;

        private InternetClientCapability(nint sid) => _sid = sid;

        public nint Sid => _sid != nint.Zero
            ? _sid
            : throw new ObjectDisposedException(nameof(InternetClientCapability));

        public static InternetClientCapability Create()
        {
            if (!NativeMethods.DeriveCapabilitySidsFromName(
                    "internetClient",
                    out var groupSids,
                    out var groupCount,
                    out var capabilitySids,
                    out var capabilityCount)
                || capabilityCount != 1)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "无法派生 internetClient capability SID。");
            }

            try
            {
                for (var index = 0u; index < groupCount; index++)
                {
                    _ = NativeMethods.LocalFree(Marshal.ReadIntPtr(groupSids, checked((int)index * nint.Size)));
                }

                return new InternetClientCapability(Marshal.ReadIntPtr(capabilitySids));
            }
            finally
            {
                _ = NativeMethods.LocalFree(groupSids);
                _ = NativeMethods.LocalFree(capabilitySids);
            }
        }

        public void Dispose()
        {
            if (_sid != nint.Zero)
            {
                _ = NativeMethods.LocalFree(_sid);
                _sid = nint.Zero;
            }
        }
    }

    private sealed class StartupAttributeList : IDisposable
    {
        private readonly nint _capabilityArray;
        private readonly nint _securityCapabilities;
        private readonly nint _handleArray;

        private StartupAttributeList(
            nint pointer,
            nint capabilityArray,
            nint securityCapabilities,
            nint handleArray)
        {
            Pointer = pointer;
            _capabilityArray = capabilityArray;
            _securityCapabilities = securityCapabilities;
            _handleArray = handleArray;
        }

        public nint Pointer { get; }

        public static StartupAttributeList Create(
            nint packageSid,
            nint capabilitySid,
            nint[] handles)
        {
            var attributeCount = handles.Length > 0 ? 2 : 1;
            nuint size = 0;
            _ = NativeMethods.InitializeProcThreadAttributeList(
                nint.Zero,
                attributeCount,
                0,
                ref size);
            if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var pointer = Marshal.AllocHGlobal(checked((int)size));
            var capabilityArray = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.SidAndAttributes>());
            var securityCapabilities = Marshal.AllocHGlobal(
                Marshal.SizeOf<NativeMethods.SecurityCapabilities>());
            var handleArray = handles.Length == 0
                ? nint.Zero
                : Marshal.AllocHGlobal(checked(handles.Length * nint.Size));
            var initialized = false;
            try
            {
                if (!NativeMethods.InitializeProcThreadAttributeList(pointer, attributeCount, 0, ref size))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                initialized = true;
                Marshal.StructureToPtr(
                    new NativeMethods.SidAndAttributes
                    {
                        Sid = capabilitySid,
                        Attributes = SeGroupEnabled,
                    },
                    capabilityArray,
                    fDeleteOld: false);
                Marshal.StructureToPtr(
                    new NativeMethods.SecurityCapabilities
                    {
                        AppContainerSid = packageSid,
                        Capabilities = capabilityArray,
                        CapabilityCount = 1,
                    },
                    securityCapabilities,
                    fDeleteOld: false);
                if (!NativeMethods.UpdateProcThreadAttribute(
                        pointer,
                        0,
                        ProcThreadAttributeSecurityCapabilities,
                        securityCapabilities,
                        (nuint)Marshal.SizeOf<NativeMethods.SecurityCapabilities>(),
                        nint.Zero,
                        nint.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                if (handles.Length > 0)
                {
                    Marshal.Copy(handles, 0, handleArray, handles.Length);
                    if (!NativeMethods.UpdateProcThreadAttribute(
                            pointer,
                            0,
                            ProcThreadAttributeHandleList,
                            handleArray,
                            checked((nuint)(handles.Length * nint.Size)),
                            nint.Zero,
                            nint.Zero))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                }

                return new StartupAttributeList(
                    pointer,
                    capabilityArray,
                    securityCapabilities,
                    handleArray);
            }
            catch
            {
                if (initialized)
                {
                    NativeMethods.DeleteProcThreadAttributeList(pointer);
                }

                Marshal.FreeHGlobal(handleArray);
                Marshal.FreeHGlobal(securityCapabilities);
                Marshal.FreeHGlobal(capabilityArray);
                Marshal.FreeHGlobal(pointer);
                throw;
            }
        }

        public void Dispose()
        {
            NativeMethods.DeleteProcThreadAttributeList(Pointer);
            Marshal.FreeHGlobal(_handleArray);
            Marshal.FreeHGlobal(_securityCapabilities);
            Marshal.FreeHGlobal(_capabilityArray);
            Marshal.FreeHGlobal(Pointer);
        }
    }

    /// <summary>在 CreateProcessW 返回前固定 SafeHandle，避免 GC 或调用方并发释放导致句柄复用。</summary>
    private sealed class InheritedHandleLease : IDisposable
    {
        private readonly IReadOnlyList<SafeHandle> _handles;

        private InheritedHandleLease(IReadOnlyList<SafeHandle> handles)
        {
            _handles = handles;
            NativeHandles = handles.Select(handle => handle.DangerousGetHandle()).ToArray();
        }

        public nint[] NativeHandles { get; }

        public static InheritedHandleLease Create(AppContainerInheritedHandles? inheritedHandles)
        {
            var handles = inheritedHandles?.EnumerateAll().Distinct().ToArray()
                ?? [];
            var leased = new List<SafeHandle>(handles.Length);
            try
            {
                foreach (var handle in handles)
                {
                    var added = false;
                    handle.DangerousAddRef(ref added);
                    if (!added)
                    {
                        throw new InvalidOperationException("无法固定继承句柄。");
                    }

                    leased.Add(handle);
                }

                return new InheritedHandleLease(leased);
            }
            catch
            {
                foreach (var handle in leased)
                {
                    handle.DangerousRelease();
                }

                throw;
            }
        }

        public void Dispose()
        {
            foreach (var handle in _handles)
            {
                handle.DangerousRelease();
            }
        }
    }
}

/// <summary>集中声明 compatibility spike 所需 Win32 interface，避免互操作细节泄漏到调用方。</summary>
[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityCapabilities
    {
        public nint AppContainerSid;
        public nint Capabilities;
        public uint CapabilityCount;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
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
        public uint Flags;
        public short ShowWindow;
        public short Reserved2;
        public nint Reserved2Pointer;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenAppContainerInformation
    {
        public nint TokenAppContainer;
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    internal static extern int CreateAppContainerProfile(
        string appContainerName,
        string displayName,
        string description,
        nint capabilities,
        uint capabilityCount,
        out nint appContainerSid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    internal static extern int DeriveAppContainerSidFromAppContainerName(
        string appContainerName,
        out nint appContainerSid);

    [DllImport("kernelbase.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeriveCapabilitySidsFromName(
        string capabilityName,
        out nint capabilityGroupSids,
        out uint capabilityGroupSidCount,
        out nint capabilitySids,
        out uint capabilitySidCount);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ConvertSidToStringSid(nint sid, out nint stringSid);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EqualSid(nint firstSid, nint secondSid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(
        nint process,
        uint desiredAccess,
        out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(
        nint token,
        int tokenInformationClass,
        nint tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern nint FreeSid(nint sid);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint LocalFree(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeProcThreadAttributeList(
        nint attributeList,
        int attributeCount,
        int flags,
        ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateProcThreadAttribute(
        nint attributeList,
        uint flags,
        nuint attribute,
        nint value,
        nuint size,
        nint previousValue,
        nint returnSize);

    [DllImport("kernel32.dll")]
    internal static extern void DeleteProcThreadAttributeList(nint attributeList);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [SuppressMessage(
        "Performance",
        "CA1838:Avoid StringBuilder parameters for P/Invokes",
        Justification = "CreateProcessW 按 Win32 约定可能修改命令行缓冲区，必须提供可写内存。")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint ResumeThread(nint thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);
}
