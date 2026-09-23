param(
    [Parameter(Mandatory)]
    [string] $LibraryPath,
    [string] $CompilerPath,
    [int] $TimeoutSeconds = 25
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $repositoryRoot 'native\Diagnostics\gamespeed_probe.c'
$outputDirectory = Join-Path $repositoryRoot 'artifacts\analysis\4399start'
$executablePath = Join-Path $outputDirectory 'gamespeed-probe.exe'
if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $CompilerPath = $env:BQTJ_MINGW32_GCC
}
if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $compilerCommand = Get-Command i686-w64-mingw32-gcc -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $compilerCommand) {
        $CompilerPath = $compilerCommand.Source
    }
}
if ([string]::IsNullOrWhiteSpace($CompilerPath) -or
    -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw '未找到 32 位 GCC。请传入 -CompilerPath、设置 BQTJ_MINGW32_GCC，或将 i686-w64-mingw32-gcc 加入 PATH。'
}

$CompilerPath = (Resolve-Path -LiteralPath $CompilerPath).Path
$compilerDirectory = Split-Path -Parent $CompilerPath

if (-not (Test-Path -LiteralPath $LibraryPath)) {
    throw "GameSpeed.dll 不存在：$LibraryPath"
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

# gcc 会启动 cc1.exe；必须让编译器子进程能解析同目录下的 MinGW 运行库。
$originalPath = $env:PATH
$env:PATH = "$compilerDirectory;$originalPath"
try {
    & $CompilerPath -municode -O0 -g -o $executablePath $sourcePath
    if ($LASTEXITCODE -ne 0) {
        throw "探针编译失败，退出码：$LASTEXITCODE"
    }
}
finally {
    $env:PATH = $originalPath
}

$process = Start-Process `
    -FilePath $executablePath `
    -ArgumentList @($LibraryPath) `
    -WorkingDirectory $outputDirectory `
    -PassThru

if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
    Stop-Process -Id $process.Id
    throw "FAIL: 探针在 $TimeoutSeconds 秒内未自行结束。"
}

if ($process.ExitCode -ne 0) {
    throw "FAIL: 最小探针异常退出，退出码：$($process.ExitCode)。"
}

'PASS: SetGameSpeed 返回后探针稳定存活。'
