param(
    [string] $OutputDirectory,
    [string] $CompilerPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts\native'
}

function Resolve-CompilerPath {
    param([string] $RequestedPath)

    # 发布构建不应依赖某台开发机的安装目录；显式参数优先，其次才是环境变量和 PATH。
    $explicitCandidates = @($RequestedPath, $env:BQTJ_MINGW32_GCC) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    foreach ($candidate in $explicitCandidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    foreach ($commandName in @('i686-w64-mingw32-gcc.exe', 'i686-w64-mingw32-gcc', 'gcc.exe', 'gcc')) {
        $command = Get-Command $commandName -CommandType Application -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -ne $command) {
            return $command.Source
        }
    }

    throw '未找到 32 位 GCC。请传入 -CompilerPath、设置 BQTJ_MINGW32_GCC，或将 i686-w64-mingw32-gcc 加入 PATH。'
}

$compiler = Resolve-CompilerPath -RequestedPath $CompilerPath

$compilerDirectory = Split-Path -Parent $compiler
$currentProcessPath = [Environment]::GetEnvironmentVariable('Path', 'Process')
[Environment]::SetEnvironmentVariable(
    'Path',
    "$compilerDirectory;$currentProcessPath",
    'Process')

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$source = Join-Path $repositoryRoot 'native\FlashHost\native_flash_host.c'
$virtualClockSource = Join-Path $repositoryRoot 'native\FlashHost\virtual_clock.c'
$output = Join-Path $OutputDirectory 'BqtjNativeFlashHost.exe'
& $compiler `
    -std=c11 `
    -Os `
    -s `
    -Wall `
    -Wextra `
    -municode `
    -mwindows `
    '-Wl,--disable-nxcompat,--disable-dynamicbase' `
    -o $output `
    $source `
    $virtualClockSource `
    -lole32 `
    -loleaut32 `
    -lshell32 `
    -lwininet `
    -luuid
if ($LASTEXITCODE -ne 0) {
    throw "原生 Flash 宿主编译失败，退出码：$LASTEXITCODE。"
}

# PE Machine=0x014c 表示 x86；在 CI 中立即拒绝误用 64 位 gcc 生成的宿主。
$stream = [System.IO.File]::OpenRead($output)
try {
    $reader = [System.IO.BinaryReader]::new($stream)
    $stream.Position = 0x3c
    $peOffset = $reader.ReadInt32()
    $stream.Position = $peOffset + 4
    $machine = $reader.ReadUInt16()
}
finally {
    $stream.Dispose()
}

if ($machine -ne 0x014c) {
    throw ('原生 Flash 宿主必须是 x86 PE，实际 Machine=0x{0:X4}。' -f $machine)
}

"Built native Flash host: $output"
