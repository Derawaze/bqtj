param(
    [Parameter(Mandatory = $true)]
    [string] $ExecutablePath,

    [int] $LoadSeconds = 8
)

$ErrorActionPreference = 'Stop'
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$gamePage = 'https://sbai.4399.com/4399swf/upload_swf/ftp15/linxy/20150324/gun/v3680d.htm'

Add-Type -AssemblyName System.Drawing.Common
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class ContainerRenderProbe
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr windowHandle, out RECT rectangle);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr windowHandle, IntPtr targetDeviceContext, uint flags);
}
'@

$arguments = @(
    '--flash-host',
    '--account-id', [guid]::NewGuid().ToString('D'),
    '--account-name', '渲染测试',
    '--game-page', $gamePage,
    '--panel-pid', $PID,
    '--layout-probe'
)
$process = $null

try {
    $process = Start-Process -FilePath $resolvedExecutable -ArgumentList $arguments -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
    } while (
        -not $process.HasExited `
        -and $process.MainWindowTitle -notmatch '\[layout:' `
        -and [DateTime]::UtcNow -lt $deadline)

    if ($process.HasExited -or $process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'FAIL: game container exited before rendering.'
    }

    Start-Sleep -Seconds $LoadSeconds
    $process.Refresh()

    $layoutMatch = [regex]::Match($process.MainWindowTitle, '\[layout:(?<width>\d+)x(?<height>\d+)\]')
    if (-not $layoutMatch.Success) {
        throw "FAIL: container did not expose its game-area size: $($process.MainWindowTitle)"
    }
    if ($layoutMatch.Groups['width'].Value -ne '950' `
        -or $layoutMatch.Groups['height'].Value -ne '600') {
        throw "FAIL: default game area is not 100% (actual=$($layoutMatch.Groups['width'].Value)x$($layoutMatch.Groups['height'].Value))."
    }

    $rectangle = New-Object ContainerRenderProbe+RECT
    if (-not [ContainerRenderProbe]::GetWindowRect($process.MainWindowHandle, [ref] $rectangle)) {
        throw 'FAIL: could not read the game-container window bounds.'
    }

    $width = $rectangle.Right - $rectangle.Left
    $height = $rectangle.Bottom - $rectangle.Top
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $deviceContext = $graphics.GetHdc()
    try {
        if (-not [ContainerRenderProbe]::PrintWindow(
            $process.MainWindowHandle,
            $deviceContext,
            2)) {
            throw 'FAIL: PrintWindow could not capture the game container.'
        }
    }
    finally {
        $graphics.ReleaseHdc($deviceContext)
        $graphics.Dispose()
    }

    $sampleCount = 0
    $darkCount = 0
    $colors = [Collections.Generic.HashSet[int]]::new()
    for ($y = 70; $y -lt $height; $y += 12) {
        for ($x = 8; $x -lt ($width - 8); $x += 12) {
            $pixel = $bitmap.GetPixel($x, $y)
            $brightness = [int] $pixel.R + [int] $pixel.G + [int] $pixel.B
            if ($brightness -lt 30) {
                $darkCount++
            }
            [void] $colors.Add($pixel.ToArgb())
            $sampleCount++
        }
    }
    $bitmap.Dispose()

    $darkRatio = $darkCount / $sampleCount
    if ($darkRatio -gt 0.90 -and $colors.Count -lt 50) {
        throw "FAIL: captured game area is effectively black (darkRatio=$([math]::Round($darkRatio, 4)), colors=$($colors.Count))."
    }

    "PASS: 100% game area rendered non-black content (950x600, darkRatio=$([math]::Round($darkRatio, 4)), colors=$($colors.Count))."
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
