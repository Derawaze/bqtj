# 日常工作进程真实入口回归；虚构句柄，不枚举/截图/点击游戏，不读取凭据或日志。
#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not ('DailyWorkerDpiProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DailyWorkerDpiProbe {
    [DllImport("shcore.dll")] private static extern int GetProcessDpiAwareness(IntPtr process, out int awareness);
    public static int Read(IntPtr process) {
        if (GetProcessDpiAwareness(process, out var awareness) != 0) throw new InvalidOperationException("无法检查本次工作进程DPI模式");
        return awareness;
    }
}
'@
}
$runIdentity = [guid]::NewGuid()
$stopSignal = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, ('Local\bqtj-daily-stop-' + $runIdentity.ToString('N')))
$info = [Diagnostics.ProcessStartInfo]::new((Join-Path $repositoryRoot 'artifacts/dev/automation/background-probe/MaaBackgroundProbe.exe'))
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardInput = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
$info.ArgumentList.Add('--daily-worker')
$probe = [Diagnostics.Process]::Start($info)
try {
    # 等入口停在标准输入，只检查自身刚创建的进程；第三方界面不参与。
    Start-Sleep -Milliseconds 400
    $awareness = [DailyWorkerDpiProbe]::Read($probe.Handle)
    if ($awareness -ne 2) { throw "日常真实入口DPI模式错误：$awareness（必须为PerMonitor=2）" }
    $request = @{Target=@{SessionId=[guid]::NewGuid(); ProfileId=[guid]::NewGuid(); ContainerProcessId=1; NativeProcessId=1; FlashWindowHandle=1; Width=950; Height=600}; SaveNumber=5; RunId=$runIdentity; ParentProcessId=$PID; AutomationDirectory=(Join-Path $repositoryRoot 'artifacts/dev/automation')}
    $probe.StandardInput.WriteLine(($request | ConvertTo-Json -Depth 4 -Compress))
    $probe.StandardInput.Close()
    if (-not $probe.WaitForExit(10000)) { throw '虚构目标启动回归超过10秒' }
    $messages = @($probe.StandardOutput.ReadToEnd().Split("`n") | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
    $failures = @($messages | Where-Object { $_.Kind -eq 'Error' })
    if ($probe.ExitCode -ne 1 -or $failures.Count -ne 1 -or $failures[0].Code -ne 2) {
        throw '虚构目标必须报告TargetChanged=2，不得吞掉失败原因或执行输入'
    }
    'PASS：真实日常入口PerMonitor DPI；虚构目标拒绝及结构化错误通过。无游戏输入。'
}
finally {
    if (-not $probe.HasExited) { $probe.Kill(); $probe.WaitForExit() }
    $probe.Dispose()
    $stopSignal.Dispose()
}
