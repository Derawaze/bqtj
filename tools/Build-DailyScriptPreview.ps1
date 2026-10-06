# 日常脚本开发包：共用artifacts/dev组件，不递增正式版本，不启动或关闭游戏。
#Requires -Version 7.0
[CmdletBinding()]
param([string] $CompilerPath)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$automationRoot = Join-Path $repositoryRoot 'artifacts/dev/automation'
$workerOutput = Join-Path $automationRoot 'background-probe'

# 正在运行的工作进程不能被覆盖；用户停止任务后重新构建。
$running = @(Get-Process -Name 'MaaBackgroundProbe' -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path.StartsWith($workerOutput + '\', [StringComparison]::OrdinalIgnoreCase) } catch { $false }
})
if ($running.Count -gt 0) { throw '请先停止日常任务或关闭人工探针，再构建组件。' }
& (Join-Path $PSScriptRoot 'Test-MaaRuntime.ps1') | Out-Host
& (Join-Path $PSScriptRoot 'Prepare-MaaOcr.ps1') -VerifyOnly | Out-Host
& dotnet build (Join-Path $repositoryRoot 'native/Diagnostics/MaaBackgroundProbe/MaaBackgroundProbe.csproj') `
    -c Release -o $workerOutput --no-restore -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw '日常视觉组件构建失败。' }
& (Join-Path $PSScriptRoot 'Start-DevLauncher.ps1') -BuildOnly -CompilerPath $CompilerPath
if ($LASTEXITCODE -ne 0) { throw '开发面板构建失败。' }
Write-Output "日常组件目录：$automationRoot（此开发组件需要本机.NET 10 x64运行库）"
