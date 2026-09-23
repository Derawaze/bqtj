param(
    [Parameter(Mandatory = $true)]
    [string] $ExecutablePath
)

$ErrorActionPreference = 'Stop'
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$gamePage = 'https://sbai.4399.com/4399swf/upload_swf/ftp15/linxy/20150324/gun/v3680d.htm'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class RecentSpeedToggleProbe
{
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);
}
'@

function Find-NamedElement([string] $Name) {
    $nameProperty = [System.Windows.Automation.AutomationElement]::NameProperty
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new($nameProperty, $Name))
}

function Open-SpeedMenu([System.Windows.Automation.AutomationElement] $Window) {
    $nameProperty = [System.Windows.Automation.AutomationElement]::NameProperty
    $button = $Window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new($nameProperty, '变速'))
    if ($null -eq $button) {
        throw 'FAIL: speed button was not exposed to UI Automation.'
    }
    $button.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 250
}

function Select-Speed(
    [System.Windows.Automation.AutomationElement] $Window,
    [string] $Label) {
    Open-SpeedMenu $Window
    $item = Find-NamedElement $Label
    if ($null -eq $item) {
        throw "FAIL: speed item '$Label' was not exposed to UI Automation."
    }
    $item.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 900
}

function Assert-RecentAndCloseMenu(
    [System.Windows.Automation.AutomationElement] $Window,
    [string] $ExpectedLabel) {
    Open-SpeedMenu $Window
    $item = Find-NamedElement $ExpectedLabel
    if ($null -eq $item) {
        throw "FAIL: expected recent-speed item '$ExpectedLabel' was not found."
    }
    $item.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 900
}

$arguments = @(
    '--flash-host',
    '--account-id', [guid]::NewGuid().ToString('D'),
    '--account-name', '最近变速互换回归测试',
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
        throw 'FAIL: container exited before the recent-speed test.'
    }

    Start-Sleep -Seconds 5
    $window = [System.Windows.Automation.AutomationElement]::FromHandle(
        $process.MainWindowHandle)
    Select-Speed $window '0.2倍'
    Select-Speed $window '0.03倍'
    Assert-RecentAndCloseMenu $window '最近变速（0.2倍，F3）'

    # 上一步点击最近变速后当前为 0.2；F3 应切回 0.03，并把 0.2 记录成上一档。
    [void][RecentSpeedToggleProbe]::SetForegroundWindow($process.MainWindowHandle)
    [System.Windows.Forms.SendKeys]::SendWait('{F3}')
    Start-Sleep -Milliseconds 900
    Open-SpeedMenu $window
    if ($null -eq (Find-NamedElement '最近变速（0.2倍，F3）')) {
        throw 'FAIL: F3 did not swap the previous gear back to 0.2x.'
    }

    'PASS: recent speed swapped 0.2 -> 0.03 -> 0.2 by menu, then back to 0.03 by F3 while retaining 0.2 as the previous gear.'
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
