param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version,
    [string] $OutputDirectory,
    [string] $CompilerPath,
    [switch] $NoRestore
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src\BqtjLauncher.Desktop\BqtjLauncher.Desktop.csproj'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts\release'
}

$resolvedRepositoryRoot = (Resolve-Path -LiteralPath $repositoryRoot).Path
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$resolvedOutputRoot = (Resolve-Path -LiteralPath $OutputDirectory).Path
if (-not $resolvedOutputRoot.StartsWith(
        "$resolvedRepositoryRoot\",
        [StringComparison]::OrdinalIgnoreCase)) {
    throw '发布输出目录必须位于仓库内，避免覆盖仓库外的文件。'
}

$packageName = "BqtjLauncher-v$Version-win-x86"
$packageDirectory = Join-Path $resolvedOutputRoot $packageName
$archivePath = Join-Path $resolvedOutputRoot "$packageName.zip"
$checksumPath = "$archivePath.sha256"

# 同版本重建时先清理它自己的临时目录，不触碰其他发布或用户数据。
if (Test-Path -LiteralPath $packageDirectory) {
    Remove-Item -LiteralPath $packageDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
if (Test-Path -LiteralPath $checksumPath) {
    Remove-Item -LiteralPath $checksumPath -Force
}

$nativeDirectory = Join-Path $resolvedOutputRoot '.native'
if (Test-Path -LiteralPath $nativeDirectory) {
    Remove-Item -LiteralPath $nativeDirectory -Recurse -Force
}

try {
    & (Join-Path $PSScriptRoot 'Build-NativeFlashHost.ps1') `
        -OutputDirectory $nativeDirectory `
        -CompilerPath $CompilerPath
    if ($LASTEXITCODE -ne 0) {
        throw "原生宿主构建失败，退出码：$LASTEXITCODE。"
    }

    $publishArguments = @(
        'publish', $projectPath,
        '--configuration', 'Release',
        '--runtime', 'win-x86',
        '--self-contained', 'true',
        '--output', $packageDirectory,
        "-p:Version=$Version",
        '-p:DebugType=none',
        '-p:DebugSymbols=false',
        # 将运行库压缩进主程序；原生游戏宿主仍独立，保持每账号的进程边界。
        '-p:PublishSingleFile=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        # 启动器界面仅提供简体中文；保留其他卫星资源会重复携带十余组 WPF/WinForms 文本。
        '-p:SatelliteResourceLanguages=zh-Hans',
        # 发布包只保留运行文件，API 文档与引用符号属于开发产物。
        '-p:PublishDocumentationFiles=false',
        '-p:PublishReferencesDocumentationFiles=false',
        '-p:PublishReferencesSymbols=false'
    )
    if ($NoRestore) {
        $publishArguments += '--no-restore'
    }

    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw ".NET 发布失败，退出码：$LASTEXITCODE。"
    }

    Copy-Item `
        -LiteralPath (Join-Path $nativeDirectory 'BqtjNativeFlashHost.exe') `
        -Destination $packageDirectory `
        -Force

    $requiredFiles = @(
        'BqtjLauncher.Desktop.exe',
        'BqtjNativeFlashHost.exe'
    )
    foreach ($requiredFile in $requiredFiles) {
        $requiredPath = Join-Path $packageDirectory $requiredFile
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "发布包缺少必要文件：$requiredFile"
        }
    }

    # 发布包保留项目说明；许可证需由维护者确认后再加入，不在此处自动生成。
    Copy-Item `
        -LiteralPath (Join-Path $repositoryRoot 'README.md') `
        -Destination $packageDirectory `
        -Force

    $noticePath = Join-Path $repositoryRoot 'THIRD_PARTY_NOTICES.md'
    if (Test-Path -LiteralPath $noticePath -PathType Leaf) {
        Copy-Item -LiteralPath $noticePath -Destination $packageDirectory -Force
    }

    $licensePath = Join-Path $repositoryRoot 'LICENSE'
    if (Test-Path -LiteralPath $licensePath -PathType Leaf) {
        Copy-Item -LiteralPath $licensePath -Destination $packageDirectory -Force
    }

    Compress-Archive -LiteralPath $packageDirectory -DestinationPath $archivePath -CompressionLevel Optimal
    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$archiveHash  $([System.IO.Path]::GetFileName($archivePath))" |
        Set-Content -LiteralPath $checksumPath -Encoding ascii -NoNewline

    # 在交给 CI 或 Release 前从 ZIP 本身复核清单，避免只检查发布暂存目录而漏掉打包问题。
    & (Join-Path $PSScriptRoot 'Test-ReleasePackage.ps1') `
        -ArchivePath $archivePath `
        -ChecksumPath $checksumPath
    if ($LASTEXITCODE -ne 0) {
        throw "发布包清单验证失败，退出码：$LASTEXITCODE。"
    }
}
finally {
    if (Test-Path -LiteralPath $nativeDirectory) {
        Remove-Item -LiteralPath $nativeDirectory -Recurse -Force
    }
}

"Created release package: $archivePath"
"Created checksum: $checksumPath"
