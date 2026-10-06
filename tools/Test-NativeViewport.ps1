param(
    [Parameter(Mandatory)] [string] $CompilerPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$compiler = (Resolve-Path -LiteralPath $CompilerPath).Path
$probeDirectory = Join-Path $repositoryRoot 'artifacts/dev/probes'
New-Item -ItemType Directory -Force -Path $probeDirectory | Out-Null
$env:Path = (Split-Path -Parent $compiler) + ';' + $env:Path

# 探针包含生产宿主源码，使用相同的 x86/LAA/Flash 兼容链接选项；只使用自制离线夹具。
$names = @('ViewportProbe', 'StartupZoomProbe', 'AddressSpaceProbe', 'NativeCommandPipeProbe')
foreach ($name in $names) {
    $source = Join-Path $repositoryRoot "native/Diagnostics/$name.c"
    $output = Join-Path $probeDirectory "$name.exe"
    & $compiler -DUNICODE -D_UNICODE -std=c11 -Os -Wall -Wextra `
        '-Wl,--disable-nxcompat,--disable-dynamicbase,--large-address-aware' `
        -o $output $source (Join-Path $repositoryRoot 'native/FlashHost/virtual_clock.c') `
        -lole32 -loleaut32 -lshell32 -lwininet -luuid -lgdi32 -lpsapi
    if ($LASTEXITCODE -ne 0) { throw "$name 编译失败。" }

    # 拒绝误用 PATH 中的 x64 GCC，避免 x64 探针假通过却没有测试32位 Flash。
    $stream = [IO.File]::OpenRead($output)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        $stream.Position = 0x3c
        $offset = $reader.ReadInt32()
        $stream.Position = $offset + 4
        if ($reader.ReadUInt16() -ne 0x014c) { throw '探针必须使用32位 GCC。' }
    }
    finally { $stream.Dispose() }

    if ($name -eq 'NativeCommandPipeProbe') {
        & (Join-Path $PSScriptRoot 'Test-NativeCommandPipe.ps1') -ProbePath $output
    }
    else { & $output }
    if ($LASTEXITCODE -ne 0) { throw "$name 验证失败。" }
}
