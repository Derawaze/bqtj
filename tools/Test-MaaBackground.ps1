# 合成窗口集成测试与人工探针构建入口；脚本本身不连接游戏，不进入正式打包链。
#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('PrintWindow', 'FramePool', 'DirectChild', 'Template', 'OCR')]
    [string] $CaptureMethod = 'PrintWindow',

    # 只构建可手动打开的真实窗口验证工具，不启动测试或连接游戏。
    [switch] $InspectorBuildOnly
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Test-MaaRuntime.ps1') | Out-Host
if ($CaptureMethod -eq 'OCR') { & (Join-Path $PSScriptRoot 'Prepare-MaaOcr.ps1') -VerifyOnly | Out-Host }
$repoRoot = Split-Path $PSScriptRoot -Parent
$root = Join-Path $repoRoot 'artifacts/dev/automation'
$output = Join-Path $root 'background-probe'
$project = Join-Path $repoRoot 'native/Diagnostics/MaaBackgroundProbe/MaaBackgroundProbe.csproj'
dotnet build $project -c Release -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw '合成窗口探针构建失败。' }
if ($InspectorBuildOnly) {
    Write-Output "人工验证工具：$(Join-Path $output 'MaaBackgroundProbe.exe')"
    return
}

$run = Join-Path $root ('synthetic-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$runtime = Join-Path $root 'maa-5.14.2-win-x64/bin'
$exe = Join-Path $output 'MaaBackgroundProbe.exe'
$arguments = @('"' + $runtime + '"', '"' + $run + '"', $CaptureMethod)
$process = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $run -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $run 'stdout.txt') -RedirectStandardError (Join-Path $run 'stderr.txt')
try {
    # 第三方原生等待/销毁也受外部硬超时保护，失败只终止自己创建的探针进程树。
    if (-not $process.WaitForExit(45000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw "探针超过45秒，已终止本次探针。合成测试产物：$run"
    }
    $process.WaitForExit()
    $resultFile = Join-Path $run 'result.json'
    if (-not (Test-Path -LiteralPath $resultFile)) {
        throw "探针未生成结果（退出码$($process.ExitCode)）。合成测试产物：$run"
    }
    $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
    $result | ConvertTo-Json
    if ($process.ExitCode -ne 0 -or -not $result.passed) { throw "后台合成测试未通过：$run" }
    $scope = if ($CaptureMethod -in @('Template', 'OCR')) { '仅合成图片识别通过，不代表游戏流程或最终保存确认' }
        else { '仅合成窗口通过，不代表Flash输入兼容' }
    Write-Output "PASS：$scope。结果：$resultFile"
}
finally {
    if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    $process.Dispose()
}
