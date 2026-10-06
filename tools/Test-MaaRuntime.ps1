# 仅验证开发依赖可加载及所需C接口存在，不枚举窗口、不截图、不创建控制器。
#Requires -Version 7.0
[CmdletBinding()]
param(
    # 离线准备时传入同一个官方ZIP，避免要求另复制到下载缓存。
    [string] $ArchivePath
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw '请在Windows x64 PowerShell 7独立进程运行此探针；不能在x86启动器中加载组件。'
}
$repoRoot = Split-Path $PSScriptRoot -Parent
$runtimeRoot = Join-Path $repoRoot 'artifacts/dev/automation/maa-5.14.2-win-x64'
$archive = Join-Path $repoRoot 'artifacts/dev/automation/MAA-win-x86_64-v5.14.2.zip'
if ($ArchivePath) { $archive = (Resolve-Path -LiteralPath $ArchivePath).Path }
if (-not (Test-Path -LiteralPath $archive)) {
    throw '缺少官方ZIP校验源，请先运行Prepare-MaaFramework.ps1。'
}

# 准备脚本负责固定ZIP来源和校验值；探针再逐文件比较，拒绝加载被改写的解压产物。
& (Join-Path $PSScriptRoot 'Prepare-MaaFramework.ps1') -ArchivePath $archive | Out-Host
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    foreach ($entry in $zip.Entries) {
        if (-not $entry.Name) { continue }
        $relative = $entry.FullName.Replace('/', [IO.Path]::DirectorySeparatorChar)
        $file = [IO.Path]::GetFullPath((Join-Path $runtimeRoot $relative))
        if (-not $file.StartsWith($runtimeRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'ZIP包含超出组件目录的路径。'
        }
        $ancestor = $file
        while ($ancestor -and $ancestor.Length -ge $runtimeRoot.Length) {
            if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "组件包含重解析路径：$ancestor"
            }
            $ancestor = [IO.Path]::GetDirectoryName($ancestor)
        }
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $hash) {
            throw "解压产物与官方ZIP不一致，未加载：$relative"
        }
    }
}
finally { $zip.Dispose() }

if (-not ('BqtjMaaRuntimeProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

/// <summary>独立进程中的依赖探针，只调用无窗口副作用的版本接口。</summary>
public static class BqtjMaaRuntimeProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr VersionFunction();

    /// <summary>限制依赖搜索到DLL目录与系统默认目录，退出前释放本次模块引用。</summary>
    public static string Check(string directory)
    {
        var modules = new List<IntPtr>();
        try
        {
            IntPtr Load(string name)
            {
                var handle = LoadLibraryExW(Path.Combine(directory, name), IntPtr.Zero, 0x1100);
                if (handle == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载 " + name);
                modules.Add(handle);
                return handle;
            }
            IntPtr Require(IntPtr module, string name)
            {
                var address = GetProcAddress(module, name);
                if (address == IntPtr.Zero) throw new MissingMethodException("缺少接口 " + name);
                return address;
            }
            string Version(IntPtr module, string name) =>
                Marshal.PtrToStringUTF8(Marshal.GetDelegateForFunctionPointer<VersionFunction>(Require(module, name))()) ?? "";

            var framework = Load("MaaFramework.dll");
            foreach (var name in new[] { "MaaWin32ControllerCreate", "MaaControllerPostConnection",
                "MaaControllerPostScreencap", "MaaControllerPostClick", "MaaControllerDestroy",
                "MaaTaskerCreate", "MaaResourceCreate" }) Require(framework, name);
            var toolkit = Load("MaaToolkit.dll");
            Require(toolkit, "MaaToolkitDesktopWindowFindAll");
            var win32 = Load("MaaWin32ControlUnit.dll");
            var version = Version(framework, "MaaVersion");
            var controllerVersion = Version(win32, "MaaWin32ControlUnitGetVersion");
            if (version.TrimStart('v') != "5.14.2" || controllerVersion.TrimStart('v') != "5.14.2")
                throw new InvalidOperationException("组件版本不匹配：" + version + " / " + controllerVersion);
            return "PASS: Framework=" + version + "; Win32=" + controllerVersion +
                "; x64加载及接口检查通过；本检查不验证Flash后台控制。";
        }
        finally
        {
            for (int i = modules.Count - 1; i >= 0; --i) FreeLibrary(modules[i]);
        }
    }
}
'@
}
[BqtjMaaRuntimeProbe]::Check((Join-Path $runtimeRoot 'bin'))
