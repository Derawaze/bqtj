param(
    [Parameter(Mandatory = $true)]
    [string] $ExecutablePath,

    [decimal] $Speed = 2,

    [int] $LoadSeconds = 3,

    [int] $StabilizationSeconds = 12
)

$ErrorActionPreference = 'Stop'
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$gamePage = 'https://sbai.4399.com/4399swf/upload_swf/ftp15/linxy/20150324/gun/v3680d.htm'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class SpeedChangeProbe
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr windowHandle,
        uint message,
        IntPtr wordParameter,
        IntPtr longParameter,
        uint flags,
        uint timeout,
        out IntPtr result);
}
'@

$arguments = @(
    '--flash-host',
    '--account-id', [guid]::NewGuid().ToString('D'),
    '--account-name', '变速崩溃回归测试',
    '--game-page', $gamePage,
    '--panel-pid', $PID
)
$process = $null

try {
    $process = Start-Process -FilePath $resolvedExecutable -ArgumentList $arguments -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
    } while (
        -not $process.HasExited `
        -and $process.MainWindowHandle -eq [IntPtr]::Zero `
        -and [DateTime]::UtcNow -lt $deadline)

    if ($process.HasExited -or $process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'FAIL: container exited before the speed action.'
    }

    Start-Sleep -Seconds $LoadSeconds
    $window = [System.Windows.Automation.AutomationElement]::FromHandle(
        $process.MainWindowHandle)
    $nameProperty = [System.Windows.Automation.AutomationElement]::NameProperty
    $speedButton = $window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new($nameProperty, '变速'))
    if ($null -eq $speedButton) {
        throw 'FAIL: speed button was not exposed to UI Automation.'
    }

    $speedButton.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 300

    $label = if ($Speed -eq 1) { '原速' } else { "$Speed`倍" }
    $speedItem = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new($nameProperty, $label))
    if ($null -eq $speedItem) {
        throw "FAIL: speed menu item '$label' was not exposed to UI Automation."
    }

    $speedItem.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $nativeHost = Get-CimInstance Win32_Process -Filter "Name = 'BqtjNativeFlashHost.exe'" |
        Where-Object { $_.ParentProcessId -eq $process.Id } |
        Select-Object -First 1
    if ($null -eq $nativeHost) {
        throw "FAIL: native Flash child exited immediately after selecting $label."
    }

    for ($second = 1; $second -le $StabilizationSeconds; $second++) {
        Start-Sleep -Seconds 1
        $nativeHost = Get-CimInstance Win32_Process -Filter "ProcessId = $($nativeHost.ProcessId)"
        if ($null -eq $nativeHost) {
            throw "FAIL: native Flash child exited $second second(s) after selecting $label."
        }
    }

    $process.Refresh()
    if ($process.HasExited) {
        throw "FAIL: container crashed after selecting $label (exitCode=$($process.ExitCode))."
    }

    $messageResult = [IntPtr]::Zero
    $sendResult = [SpeedChangeProbe]::SendMessageTimeout(
        $process.MainWindowHandle,
        0,
        [IntPtr]::Zero,
        [IntPtr]::Zero,
        2,
        1000,
        [ref] $messageResult)
    if ($sendResult -eq [IntPtr]::Zero) {
        throw "FAIL: container stopped responding after selecting $label."
    }

    "PASS: container and native Flash child remained alive for $StabilizationSeconds seconds after selecting $label."
}
finally {
    if ($null -ne $process) {
        $process.Refresh()
        if (-not $process.HasExited) {
            $null = $process.CloseMainWindow()
            if (-not $process.WaitForExit(3000)) {
                Stop-Process -Id $process.Id
            }
        }
    }
}
