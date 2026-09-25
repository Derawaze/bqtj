param(
    [switch] $NoBuild,
    [switch] $BuildOnly,
    [switch] $PassThru,
    [string] $CompilerPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src\BqtjLauncher.Desktop\BqtjLauncher.Desktop.csproj'
$developmentRoot = Join-Path $repositoryRoot 'artifacts\dev'
$slotDirectories = @(
    (Join-Path $developmentRoot 'slot-a'),
    (Join-Path $developmentRoot 'slot-b')
)

function Get-RunningExecutablePaths {
    @(Get-Process -Name 'BqtjLauncher.Desktop', 'BqtjNativeFlashHost' -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Path } catch { $null }
    } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Get-LatestBuiltSlot {
    $candidates = $slotDirectories | ForEach-Object {
        $candidate = Join-Path $_ '.preview-ready'
        if (Test-Path -LiteralPath $candidate) {
            Get-Item -LiteralPath $candidate
        }
    }
    $latest = $candidates | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($null -eq $latest) {
        throw 'No development build exists. Run without -NoBuild first.'
    }

    Split-Path -Parent $latest.FullName
}

if (-not $NoBuild) {
    $runningPaths = Get-RunningExecutablePaths
    $outputDirectory = $slotDirectories | Where-Object {
        $slot = $_
        -not ($runningPaths | Where-Object {
            $_.StartsWith($slot + '\', [StringComparison]::OrdinalIgnoreCase)
        })
    } | Select-Object -First 1

    if ($null -eq $outputDirectory) {
        throw 'Both development slots are currently running. Close one development launcher and retry.'
    }

    # 成功标记只在所有输出完成后写入，失败构建不能被-NoBuild选为最新版本。
    $readyPath = Join-Path $outputDirectory '.preview-ready'
    if (Test-Path -LiteralPath $readyPath) { Remove-Item -LiteralPath $readyPath }

    $nativeOutputDirectory = Join-Path $repositoryRoot 'artifacts\native'
    & (Join-Path $PSScriptRoot 'Build-NativeFlashHost.ps1') -OutputDirectory $nativeOutputDirectory -CompilerPath $CompilerPath
    if ($LASTEXITCODE -ne 0) {
        throw "Native Flash host build failed with exit code $LASTEXITCODE."
    }

    & dotnet build $projectPath -c Debug -r win-x86 --self-contained true -o $outputDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Debug build failed with exit code $LASTEXITCODE."
    }

    Copy-Item `
        -LiteralPath (Join-Path $nativeOutputDirectory 'BqtjNativeFlashHost.exe') `
        -Destination $outputDirectory `
        -Force
    [IO.File]::WriteAllText($readyPath, [DateTimeOffset]::UtcNow.ToString('o'))
}
else {
    $outputDirectory = Get-LatestBuiltSlot
}

$executablePath = Join-Path $outputDirectory 'BqtjLauncher.Desktop.exe'

if (-not (Test-Path -LiteralPath $executablePath)) {
    throw "Debug executable was not found: $executablePath"
}

if ($BuildOnly) {
    "Built preview: $executablePath"
    return
}
$launched = Start-Process -FilePath $executablePath -WorkingDirectory $outputDirectory -PassThru
Write-Host "Started Debug launcher from rotating slot: $executablePath"
if ($PassThru) { $launched } else { $launched.Dispose() }
