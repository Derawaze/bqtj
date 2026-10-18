#Requires -Version 7.0
[CmdletBinding()]
param([string] $Compiler64Path, [string] $ComponentDirectory, [switch] $UiOnly, [switch] $AllowNonInteractive)

# 合成夹具仅创建自己的窗口、账号库和图片；不执行生产App或读取用户数据。
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repositoryRoot ('artifacts/dev/probes/script-management-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,6))
New-Item -ItemType Directory -Path $output -Force | Out-Null
if (-not $UiOnly) {
    if (-not $Compiler64Path) { $Compiler64Path = $env:BQTJ_MINGW64_GXX }
    if (-not $Compiler64Path) { $Compiler64Path = (Get-Command g++ -ErrorAction Stop).Source }
    if (-not $ComponentDirectory) { $ComponentDirectory = Join-Path $repositoryRoot 'artifacts/dev/components/automation' }
    & (Join-Path $PSScriptRoot 'Build-ScriptComponent.ps1') -Compiler64Path $Compiler64Path -OutputDirectory $ComponentDirectory
    $nativeProbe = Join-Path $repositoryRoot 'artifacts/dev/probes/script-vision/ScriptVisionProbe.exe'
    New-Item -ItemType Directory -Path (Split-Path $nativeProbe -Parent) -Force | Out-Null
    & $Compiler64Path -std=c++20 -O2 -static -municode (Join-Path $repositoryRoot 'native/Diagnostics/ScriptVisionProbe.cpp') -o $nativeProbe -lgdi32
    if ($LASTEXITCODE -ne 0) { throw '原生视觉夹具构建失败。' }
    if ($AllowNonInteractive) { & $nativeProbe $ComponentDirectory --headless } else { & $nativeProbe $ComponentDirectory }
    if ($LASTEXITCODE -ne 0) { throw '原生视觉回归失败。' }
}
$project = Join-Path $repositoryRoot 'native/Diagnostics/ScriptManagerUiProbe/ScriptManagerUiProbe.csproj'
$build = Join-Path $output 'build'
& dotnet build $project -c Release -o $build -p:NuGetAudit=false --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw '脚本窗口夹具构建失败。' }
$info = [Diagnostics.ProcessStartInfo]::new((Join-Path $build 'BqtjLauncher.Desktop.exe'))
$info.UseShellExecute = $false; $info.CreateNoWindow = $true
$info.ArgumentList.Add($repositoryRoot); $info.ArgumentList.Add($output)
if (-not $UiOnly) { $info.ArgumentList.Add($ComponentDirectory) }
$process = [Diagnostics.Process]::Start($info)
try {
    if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw '脚本窗口夹具超时。' }
    $result = [IO.File]::ReadAllText((Join-Path $output 'result.txt'))
    if ($process.ExitCode -ne 0 -or -not $result.StartsWith('PASS:')) { throw $result }
    Write-Output $result
    Write-Output $output
} finally { $process.Dispose() }
