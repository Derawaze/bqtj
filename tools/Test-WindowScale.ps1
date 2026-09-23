param(
    [Parameter(Mandatory = $true)]
    [string] $ExecutablePath,

    [ValidateSet('1', '1.5', '2')]
    [decimal] $Scale = 1.5,

    [switch] $FullScreen,

    [int] $HoldSeconds = 5
)

$ErrorActionPreference = 'Stop'
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$gamePage = 'https://sbai.4399.com/4399swf/upload_swf/ftp15/linxy/20150324/gun/v3680d.htm'
$expectedWidth = [int](950 * $Scale)
$expectedHeight = [int](600 * $Scale)
$label = if ($FullScreen) { '全屏' } else { "{0}%" -f [int](100 * $Scale) }

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>只读枚举启动器子窗口，用于确认 Flash 画面随容器一起缩放。</summary>
public static class FlashChildWindowProbe
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

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr window, out Rect rect);

    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
'@

function Get-LayoutSize([Diagnostics.Process] $Process) {
    $Process.Refresh()
    $match = [regex]::Match(
        $Process.MainWindowTitle,
        '\[layout:(?<width>\d+)x(?<height>\d+)\]')
    if (-not $match.Success) {
        return $null
    }

    [pscustomobject]@{
        Width = [int]$match.Groups['width'].Value
        Height = [int]$match.Groups['height'].Value
    }
}

function Test-NearTarget($Layout) {
    if ($null -eq $Layout) {
        return $false
    }

    if ($FullScreen) {
        return $Layout.Width -ge 950 -and $Layout.Height -ge 600
    }

    if ($Scale -lt 2) {
        return [math]::Abs($Layout.Width - $expectedWidth) -le 1 `
            -and [math]::Abs($Layout.Height - $expectedHeight) -le 1
    }

    # 200% 高于部分显示器的窗口上限；允许等比回退，但禁止超出目标或低于 150%。
    $aspectDifference = [math]::Abs(
        ($Layout.Width * 600) - ($Layout.Height * 950))
    return $Layout.Width -le ($expectedWidth + 1) `
        -and $Layout.Height -le ($expectedHeight + 1) `
        -and $Layout.Width -ge 1424 `
        -and $Layout.Height -ge 899 `
        -and $aspectDifference -le 950
}

function Get-FlashChildSize([IntPtr] $WindowHandle) {
    $result = $null
    $callback = [FlashChildWindowProbe+EnumWindowProc] {
        param([IntPtr] $child, [IntPtr] $state)

        $className = [Text.StringBuilder]::new(128)
        [void][FlashChildWindowProbe]::GetClassName(
            $child,
            $className,
            $className.Capacity)
        if ($className.ToString() -ne 'MacromediaFlashPlayerActiveX') {
            return $true
        }

        $rect = [FlashChildWindowProbe+Rect]::new()
        if ([FlashChildWindowProbe]::GetClientRect($child, [ref] $rect)) {
            $script:result = [pscustomobject]@{
                Width = $rect.Right - $rect.Left
                Height = $rect.Bottom - $rect.Top
            }
        }

        return $false
    }

    [void][FlashChildWindowProbe]::EnumChildWindows(
        $WindowHandle,
        $callback,
        [IntPtr]::Zero)
    return $result
}

function Test-FlashFillsLayout($Layout, $FlashSize) {
    return $null -ne $Layout `
        -and $null -ne $FlashSize `
        -and [math]::Abs($FlashSize.Width - $Layout.Width) -le 6 `
        -and [math]::Abs($FlashSize.Height - $Layout.Height) -le 6
}

$arguments = @(
    '--flash-host',
    '--account-id', [guid]::NewGuid().ToString('D'),
    '--account-name', '窗口缩放回归测试',
    '--game-page', $gamePage,
    '--panel-pid', $PID,
    '--layout-probe'
)
$process = $null

try {
    $process = Start-Process -FilePath $resolvedExecutable -ArgumentList $arguments -PassThru
    $startupDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
    } while (
        -not $process.HasExited `
        -and ($process.MainWindowHandle -eq [IntPtr]::Zero `
            -or $process.MainWindowTitle -notmatch '\[layout:') `
        -and [DateTime]::UtcNow -lt $startupDeadline)

    if ($process.HasExited -or $process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'FAIL: game container exited before the scale action.'
    }

    $window = [System.Windows.Automation.AutomationElement]::FromHandle(
        $process.MainWindowHandle)
    $nameProperty = [System.Windows.Automation.AutomationElement]::NameProperty
    $scaleButton = $window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new($nameProperty, '窗口'))
    if ($null -eq $scaleButton) {
        throw 'FAIL: window-scale button was not exposed to UI Automation.'
    }

    $scaleButton.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $menuDeadline = [DateTime]::UtcNow.AddSeconds(3)
    do {
        Start-Sleep -Milliseconds 100
        $scaleItem = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new($nameProperty, $label))
    } while ($null -eq $scaleItem -and [DateTime]::UtcNow -lt $menuDeadline)
    if ($null -eq $scaleItem) {
        throw "FAIL: scale menu item '$label' was not exposed to UI Automation."
    }

    $scaleItem.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    # 同时等待外层布局和 Flash ActiveX 子窗口稳定，避免“窗口变大但画面仍在左上角”的假通过。
    $scaleDeadline = [DateTime]::UtcNow.AddSeconds(8)
    do {
        Start-Sleep -Milliseconds 200
        $layout = Get-LayoutSize $process
        $flashSize = Get-FlashChildSize $process.MainWindowHandle
    } while ((-not (Test-NearTarget $layout) `
            -or -not (Test-FlashFillsLayout $layout $flashSize)) `
        -and [DateTime]::UtcNow -lt $scaleDeadline)

    if (-not (Test-NearTarget $layout)) {
        $expectation = if ($FullScreen) {
            'a fullscreen area at least 950x600'
        }
        else {
            "a requested or proportionally fitted ${expectedWidth}x${expectedHeight} area"
        }
        throw "FAIL: $label did not reach $expectation (actual=$($layout.Width)x$($layout.Height))."
    }
    if (-not (Test-FlashFillsLayout $layout $flashSize)) {
        $actualFlash = if ($null -eq $flashSize) {
            'not found'
        }
        else {
            "$($flashSize.Width)x$($flashSize.Height)"
        }
        throw "FAIL: Flash content did not fill the $($layout.Width)x$($layout.Height) layout (actual=$actualFlash)."
    }

    $holdDeadline = [DateTime]::UtcNow.AddSeconds($HoldSeconds)
    while ([DateTime]::UtcNow -lt $holdDeadline) {
        Start-Sleep -Milliseconds 250
        $layout = Get-LayoutSize $process
        $flashSize = Get-FlashChildSize $process.MainWindowHandle
        if (-not (Test-NearTarget $layout)) {
            throw "FAIL: $label reverted during stabilization (actual=$($layout.Width)x$($layout.Height))."
        }
        if (-not (Test-FlashFillsLayout $layout $flashSize)) {
            throw "FAIL: Flash content stopped filling the layout during stabilization."
        }
    }

    "PASS: $label remained contained at $($layout.Width)x$($layout.Height); Flash=$($flashSize.Width)x$($flashSize.Height) for $HoldSeconds seconds."
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
