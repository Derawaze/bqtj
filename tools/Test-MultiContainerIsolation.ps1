param(
    [Parameter(Mandatory = $true)]
    [string] $ExecutablePath,

    [int] $StartupTimeoutSeconds = 12,

    [int] $StabilitySeconds = 10
)

$ErrorActionPreference = 'Stop'
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$gamePage = 'https://sbai.4399.com/4399swf/upload_swf/ftp15/linxy/20150324/gun/v3680d.htm'
$processes = [Collections.Generic.List[Diagnostics.Process]]::new()

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class WindowResponsivenessProbe
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr windowHandle,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeoutMilliseconds,
        out UIntPtr result);
}
'@

function Test-WindowResponsive([Diagnostics.Process] $Process) {
    $Process.Refresh()
    if ($Process.HasExited -or $Process.MainWindowHandle -eq [IntPtr]::Zero) {
        return $false
    }

    $result = [UIntPtr]::Zero
    $sendResult = [WindowResponsivenessProbe]::SendMessageTimeout(
        $Process.MainWindowHandle,
        0,
        [UIntPtr]::Zero,
        [IntPtr]::Zero,
        2,
        750,
        [ref] $result)
    return $sendResult -ne [IntPtr]::Zero
}

function Test-WindowFitsNativeGame([Diagnostics.Process] $Process) {
    $Process.Refresh()
    # 当前容器只在标题暴露实际承载区尺寸；原生/Flash 边界由渲染回归单独验证。
    $currentMatch = [regex]::Match(
        $Process.MainWindowTitle,
        '\[layout:(?<width>\d+)x(?<height>\d+)\]')
    if ($currentMatch.Success) {
        return $currentMatch.Groups['width'].Value -eq '950' `
            -and $currentMatch.Groups['height'].Value -eq '600'
    }

    # 兼容曾经输出 viewport/surface/flash 六段尺寸的开发构建。
    $match = [regex]::Match(
        $Process.MainWindowTitle,
        '\[layout:(?<viewportWidth>\d+)x(?<viewportHeight>\d+)/(?<surfaceWidth>\d+)x(?<surfaceHeight>\d+)/(?<flashWidth>\d+)x(?<flashHeight>\d+)\]')
    if (-not $match.Success) {
        return $false
    }

    return $match.Groups['viewportWidth'].Value -eq '950' `
        -and $match.Groups['viewportHeight'].Value -eq '600' `
        -and $match.Groups['surfaceWidth'].Value -eq '950' `
        -and $match.Groups['surfaceHeight'].Value -eq '600' `
        -and $match.Groups['flashWidth'].Value -eq '950' `
        -and $match.Groups['flashHeight'].Value -eq '600'
}

try {
    foreach ($index in 1..2) {
        $arguments = @(
            '--flash-host',
            '--account-id', [guid]::NewGuid().ToString('D'),
            '--account-name', "隔离测试账号 $index",
            '--game-page', $gamePage,
            '--panel-pid', $PID,
            '--layout-probe'
        )
        $process = Start-Process -FilePath $resolvedExecutable -ArgumentList $arguments -PassThru
        $processes.Add($process)
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 250
        foreach ($process in $processes) {
            $process.Refresh()
        }
        $ready = @($processes | Where-Object {
            -not $_.HasExited `
                -and $_.MainWindowTitle -like '爆枪突击 - 隔离测试账号*' `
                -and (Test-WindowFitsNativeGame $_)
        }).Count
    } while ($ready -lt 2 -and [DateTime]::UtcNow -lt $deadline)

    $states = $processes | ForEach-Object {
        $_.Refresh()
        [pscustomobject]@{
            Id = $_.Id
            HasExited = $_.HasExited
            Title = $_.MainWindowTitle
        }
    }
    $states | Format-Table -AutoSize | Out-String | Write-Host

    if ($ready -ne 2) {
        throw "FAIL: expected two independent game-container processes, ready=$ready."
    }

    foreach ($process in $processes) {
        if (-not (Test-WindowResponsive $process)) {
            throw "FAIL: game-container process $($process.Id) has a window but is not responding."
        }
        if (-not (Test-WindowFitsNativeGame $process)) {
            throw "FAIL: game-container process $($process.Id) did not fit its window to the native 950x600 game canvas."
        }
    }

    $stabilityDeadline = [DateTime]::UtcNow.AddSeconds($StabilitySeconds)
    while ([DateTime]::UtcNow -lt $stabilityDeadline) {
        Start-Sleep -Milliseconds 250
        foreach ($process in $processes) {
            $process.Refresh()
            if ($process.HasExited) {
                throw "FAIL: game-container process $($process.Id) exited during the stability window."
            }
            if (-not (Test-WindowResponsive $process)) {
                throw "FAIL: game-container process $($process.Id) stopped responding during the stability window."
            }
        }
    }

    "PASS: two independent game-container processes remained responsive with 950x600 fitted viewports."
}
finally {
    foreach ($process in $processes) {
        $process.Refresh()
        if (-not $process.HasExited) {
            $null = $process.CloseMainWindow()
            if (-not $process.WaitForExit(3000)) {
                Stop-Process -Id $process.Id
            }
        }
    }
}
