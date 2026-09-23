param(
    [Parameter(Mandatory = $true)]
    [string] $ExecutablePath,

    [decimal[]] $Speeds = @(20, 100, 20, 5, 100, 1),

    [int] $LoadSeconds = 6,

    [int] $SettleMilliseconds = 800,

    [int] $MaxCommitMilliseconds = 0
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

/// <summary>定位 Flash 子窗口并探测其消息循环，捕获“外壳正常但游戏卡死”。</summary>
public static class SpeedSwitchSequenceProbe
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
    $callback = [SpeedSwitchSequenceProbe+EnumWindowProc] {
        param([IntPtr] $child, [IntPtr] $state)

        $className = [Text.StringBuilder]::new(128)
        [void][SpeedSwitchSequenceProbe]::GetClassName(
            $child,
            $className,
            $className.Capacity)
        if ($className.ToString() -eq 'MacromediaFlashPlayerActiveX') {
            $script:flashWindow = $child
            return $false
        }
        return $true
    }

    [void][SpeedSwitchSequenceProbe]::EnumChildWindows(
        $ParentWindow,
        $callback,
        [IntPtr]::Zero)
    return $flashWindow
}

function Assert-FlashResponsive([Diagnostics.Process] $Process, [string] $Step) {
    $Process.Refresh()
    if ($Process.HasExited) {
        throw "FAIL: container exited after $Step."
    }

    $flashWindow = Get-FlashWindow $Process.MainWindowHandle
    if ($flashWindow -eq [IntPtr]::Zero) {
        throw "FAIL: Flash window disappeared after $Step."
    }

    $messageResult = [IntPtr]::Zero
    $sendResult = [SpeedSwitchSequenceProbe]::SendMessageTimeout(
        $flashWindow,
        0,
        [IntPtr]::Zero,
        [IntPtr]::Zero,
        2,
        1000,
        [ref] $messageResult)
    if ($sendResult -eq [IntPtr]::Zero) {
        throw "FAIL: Flash stopped responding after $Step."
    }
}

function Set-Speed(
    [System.Windows.Automation.AutomationElement] $Window,
    [decimal] $Speed) {
    $nameProperty = [System.Windows.Automation.AutomationElement]::NameProperty
    $speedButton = $Window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new($nameProperty, '变速'))
    if ($null -eq $speedButton) {
        throw 'FAIL: speed button was not exposed to UI Automation.'
    }

    $speedButton.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 200
    $label = if ($Speed -eq 1) { '原速' } else { "$Speed`倍" }
    $speedItem = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new($nameProperty, $label))
    if ($null -eq $speedItem) {
        throw "FAIL: speed menu item '$label' was not exposed to UI Automation."
    }

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $speedItem.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    if ($MaxCommitMilliseconds -gt 0) {
        $displayText = if ($Speed -eq 1) { '原速' } else { "$Speed`倍" }
        $commitDeadline = [DateTime]::UtcNow.AddSeconds(5)
        do {
            Start-Sleep -Milliseconds 10
            $helpText = [string] $speedButton.GetCurrentPropertyValue(
                [System.Windows.Automation.AutomationElement]::HelpTextProperty)
        } while ($helpText -notlike "当前：$displayText*" `
            -and [DateTime]::UtcNow -lt $commitDeadline)
        $stopwatch.Stop()

        if ($helpText -notlike "当前：$displayText*") {
            throw "FAIL: $displayText was not committed within 5 seconds."
        }
        if ($stopwatch.ElapsedMilliseconds -gt $MaxCommitMilliseconds) {
            throw "FAIL: $displayText took $($stopwatch.ElapsedMilliseconds) ms to commit (limit=$MaxCommitMilliseconds ms)."
        }
    }
}

$arguments = @(
    '--flash-host',
    '--account-id', [guid]::NewGuid().ToString('D'),
    '--account-name', '高倍速切换回归测试',
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
    } while (-not $process.HasExited `
        -and $process.MainWindowHandle -eq [IntPtr]::Zero `
        -and [DateTime]::UtcNow -lt $deadline)

    if ($process.HasExited -or $process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'FAIL: container exited before the speed sequence.'
    }

    Start-Sleep -Seconds $LoadSeconds
    Assert-FlashResponsive $process 'initial load'
    $window = [System.Windows.Automation.AutomationElement]::FromHandle(
        $process.MainWindowHandle)

    foreach ($speed in $Speeds) {
        Set-Speed $window $speed
        Start-Sleep -Milliseconds $SettleMilliseconds
        Assert-FlashResponsive $process "$speed x"
    }

    "PASS: Flash remained responsive through speed sequence: $($Speeds -join ' -> ')."
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
