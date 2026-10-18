#Requires -Version 7.0
[CmdletBinding()]
param([string] $Compiler64Path, [string] $MaaDirectory, [string] $ModelDirectory,
      [string] $VcRuntimeDirectory, [string] $OutputDirectory)

# 原生视觉组件独立构建，不额外发布.NET；所有生成物仅在artifacts/dev。
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$developmentRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts/dev'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $developmentRoot 'components/automation' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith($developmentRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '视觉组件只能构建到artifacts/dev。' }
if (-not $MaaDirectory) { $MaaDirectory = Join-Path $developmentRoot 'automation/maa-5.14.2-win-x64' }
if (-not $ModelDirectory) { $ModelDirectory = Join-Path $developmentRoot 'automation/ppocr-v4-zh-cn' }
if (-not (Test-Path -LiteralPath $MaaDirectory)) { & (Join-Path $PSScriptRoot 'Prepare-MaaFramework.ps1') }
if (-not (Test-Path -LiteralPath $ModelDirectory)) { & (Join-Path $PSScriptRoot 'Prepare-MaaOcr.ps1') }
if (-not $Compiler64Path) { $Compiler64Path = $env:BQTJ_MINGW64_GXX }
if (-not $Compiler64Path) { $Compiler64Path = (Get-Command g++ -ErrorAction Stop).Source }
if ((& $Compiler64Path -dumpmachine) -notmatch '^x86_64-') { throw '视觉组件必须使用x64 MinGW-w64 g++。' }
if (-not $VcRuntimeDirectory) { $VcRuntimeDirectory = [Environment]::GetFolderPath('System') }
$running = @(Get-Process -Name BqtjScriptVision -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq (Join-Path $output 'BqtjScriptVision.exe') } catch { $false } })
if ($running.Count) { throw '请先停止使用此组件的脚本队列，再构建。' }
New-Item -ItemType Directory -Path (Join-Path $output 'bin') -Force | Out-Null
# 仅携带CPU识别、Win32截图和OCR，不复制ADB、手柄、示例、GPU或开发符号。
foreach ($name in @('MaaFramework.dll','MaaWin32ControlUnit.dll','MaaUtils.dll','opencv_world4_maa.dll','onnxruntime_maa.dll','fastdeploy_ppocr_maa.dll')) {
    Copy-Item -LiteralPath (Join-Path $MaaDirectory ('bin/' + $name)) -Destination (Join-Path $output 'bin') -Force
}
# v14运行库按应用本地方式携带；可指定Visual Studio Redist目录，不要求用户另装。
foreach ($name in @('msvcp140.dll','msvcp140_1.dll','vcruntime140.dll','vcruntime140_1.dll')) {
    Copy-Item -LiteralPath (Join-Path $VcRuntimeDirectory $name) -Destination (Join-Path $output 'bin') -Force
}
New-Item -ItemType Directory -Path (Join-Path $output 'ppocr-v4-zh-cn/model/ocr') -Force | Out-Null
$modelManifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'maa-ocr-models.json') -Raw | ConvertFrom-Json
foreach ($entry in $modelManifest.files.psobject.Properties) {
    $path = Join-Path $ModelDirectory ('model/ocr/' + $entry.Name)
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Value) { throw ('OCR模型校验失败：' + $entry.Name) }
    Copy-Item -LiteralPath $path -Destination (Join-Path $output 'ppocr-v4-zh-cn/model/ocr') -Force
}
$compileArguments = @('-std=c++20','-O2','-static','-municode','-I',(Join-Path $MaaDirectory 'include'),
    (Join-Path $repositoryRoot 'native/ScriptVision/script_vision.cpp'),'-o',(Join-Path $output 'BqtjScriptVision.exe'))
& $Compiler64Path @compileArguments
if ($LASTEXITCODE -ne 0) { throw '原生视觉进程构建失败。' }
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $output 'LICENSE') -Force
New-Item -ItemType Directory -Path (Join-Path $output 'licenses') -Force | Out-Null
Copy-Item -Path (Join-Path $repositoryRoot 'resources/automation-licenses/*') -Destination (Join-Path $output 'licenses') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ScriptComponent.README.md') -Destination (Join-Path $output 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $output 'THIRD_PARTY_NOTICES.md') -Force
@{ version = 1; protocolVersion = 2; maaVersion = '5.14.2'; architecture = 'x64'; inference = 'cpu' } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $output 'component.json') -Encoding utf8
Write-Output $output
