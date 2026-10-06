param([Parameter(Mandatory)] [string] $CompilerPath)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$compiler = (Resolve-Path -LiteralPath $CompilerPath).Path
$probeDirectory = Join-Path $repositoryRoot 'artifacts/dev/probes/command-recovery'
New-Item -ItemType Directory -Force -Path $probeDirectory | Out-Null
$env:Path = (Split-Path -Parent $compiler) + ';' + $env:Path
$hostProbe = Join-Path $probeDirectory 'CommandRecoveryHostProbe.exe'

# 两端均使用生产代码：原生线程在虚构窗口上运行，托管通道使用实际匿名进程管道。
& $compiler -DUNICODE -D_UNICODE -std=c11 -Os -Wall -Wextra `
    '-Wl,--disable-nxcompat,--disable-dynamicbase,--large-address-aware' `
    -o $hostProbe (Join-Path $repositoryRoot 'native/Diagnostics/CommandRecoveryHostProbe.c') `
    (Join-Path $repositoryRoot 'native/FlashHost/virtual_clock.c') `
    -lole32 -loleaut32 -lshell32 -lwininet -luuid -lgdi32
if ($LASTEXITCODE -ne 0) { throw '原生命令恢复夹具编译失败。' }
$probeBytes = [IO.File]::ReadAllBytes($hostProbe)
$peOffset = [BitConverter]::ToInt32($probeBytes, 0x3c)
if ([BitConverter]::ToUInt16($probeBytes, $peOffset + 4) -ne 0x014c) { throw '原生夹具必须为 x86。' }
Copy-Item -LiteralPath $hostProbe -Destination (Join-Path $probeDirectory 'BqtjNativeFlashHost.exe') -Force

& dotnet build (Join-Path $repositoryRoot 'native/Diagnostics/NativeCommandRecoveryProbe') `
    -c Release --nologo -o $probeDirectory -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw '托管命令恢复探针编译失败。' }
& dotnet (Join-Path $probeDirectory 'NativeCommandRecoveryProbe.dll') $hostProbe
if ($LASTEXITCODE -ne 0) { throw '匿名管道与原生命令恢复验证失败。' }
