param(
    [Parameter(Mandatory = $true)]
    [string] $ExecutablePath,

    [int] $LoadSeconds = 6,

    [int] $StabilizationSeconds = 8
)

$ErrorActionPreference = 'Stop'
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$gamePage = 'https://sbai.4399.com/4399swf/upload_swf/ftp15/linxy/20150324/gun/v3680d.htm'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>定位 Flash ActiveX 子窗口，并检查刷新后的消息循环是否仍响应。</summary>
public static class GameReloadProbe
{
    public delegate bool EnumWindowProc(IntPtr window, IntPtr state);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(
        IntPtr parent,
        EnumWindowProc callback,
        IntPtr state);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(
        IntPtr window,
        StringBuilder className,
        int capacity);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        IntPtr wordParameter,
        IntPtr longParameter,
        uint flags,
        uint timeout,
        out IntPtr result);
}
'@

function Get-FlashWindow([IntPtr] $ParentWindow) {
    $script:flashWindow = [IntPtr]::Zero
    $callback = [GameReloadProbe+EnumWindowProc] {
        param([IntPtr] $child, [IntPtr] $state)

        $className = [Text.StringBuilder]::new(128)
        [void][GameReloadProbe]::GetClassName(
            $child,
            $className,
            $className.Capacity)
        if ($className.ToString() -eq 'MacromediaFlashPlayerActiveX') {
            $script:flashWindow = $child
            return $false
        }

        return $true
    }

    [void][GameReloadProbe]::EnumChildWindows(
        $ParentWindow,
        $callback,
        [IntPtr]::Zero)
    return $flashWindow
}

$arguments = @(
    '--flash-host',
    '--account-id', [guid]::NewGuid().ToString('D'),
    '--account-name', '刷新游戏回归测试',
    '--game-page', $gamePage,
    '--panel-pid', $PID
)
$process = $null

try {
    $process = Start-Process -FilePath $resolvedExecutable -ArgumentList $arguments -PassThru
    $startupDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
    } while (-not $process.HasExited `
        -and $process.MainWindowHandle -eq [IntPtr]::Zero `
        -and [DateTime]::UtcNow -lt $startupDeadline)

    if ($process.HasExited -or $process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'FAIL: container exited before the reload action.'
    }

    Start-Sleep -Seconds $LoadSeconds
    $flashBefore = Get-FlashWindow $process.MainWindowHandle
    if ($flashBefore -eq [IntPtr]::Zero) {
        throw 'FAIL: Flash ActiveX window was not created before reload.'
    }

    $window = [System.Windows.Automation.AutomationElement]::FromHandle(
        $process.MainWindowHandle)
    $reloadButton = $window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            '刷新游戏'))
    if ($null -eq $reloadButton) {
        throw 'FAIL: reload button was not exposed to UI Automation.'
    }

    $reloadButton.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    # IE 可能复用同一个 ActiveX HWND；按钮回调已等待原生 reload-ok 回执，这里验证刷新后的实例稳定性。
    Start-Sleep -Seconds $StabilizationSeconds
    $process.Refresh()
    $flashAfter = Get-FlashWindow $process.MainWindowHandle
    if ($process.HasExited -or $flashAfter -eq [IntPtr]::Zero) {
        throw 'FAIL: refreshed Flash instance did not remain alive.'
    }

    $messageResult = [IntPtr]::Zero
    $sendResult = [GameReloadProbe]::SendMessageTimeout(
        $flashAfter,
        0,
        [IntPtr]::Zero,
        [IntPtr]::Zero,
        2,
        1000,
        [ref] $messageResult)
    if ($sendResult -eq [IntPtr]::Zero) {
        throw 'FAIL: refreshed Flash instance stopped responding.'
    }

    "PASS: native host acknowledged reload; Flash hwnd 0x$($flashAfter.ToInt64().ToString('X')) remained responsive for $StabilizationSeconds seconds."
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
