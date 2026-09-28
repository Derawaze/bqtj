param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version,
    [string] $OutputDirectory,
    [string] $CompilerPath,
    [switch] $NoRestore,
    [switch] $Development,
    [switch] $ReleaseApproved,
    # 热更新包只交付两个入口程序和修复说明，供用户覆盖到已解压的安装目录。
    [switch] $HotUpdate
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src\BqtjLauncher.Desktop\BqtjLauncher.Desktop.csproj'
# 构建模式显式分流；参数只声明授权，实际手动验收必须由操作者核对。
if ($Development) {
    if ($ReleaseApproved -or $Version -notmatch '^0\.0\.0-dev\.\d{14}$') {
        throw '开发包必须使用 0.0.0-dev.<UTC时间戳>，不可声明正式发布授权。'
    }
    $expectedOutput = Join-Path $repositoryRoot 'artifacts\dev'
} else {
    if (-not $ReleaseApproved -or $Version -notmatch '^\d+\.\d+\.\d+$') {
        throw '正式包需要用户手动验收并明确下令；核实后传入 -ReleaseApproved 和正式版本号。'
    }
    $expectedOutput = Join-Path $repositoryRoot 'artifacts\release'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = $expectedOutput }
$resolvedOutputRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if ($resolvedOutputRoot -ne [IO.Path]::GetFullPath($expectedOutput).TrimEnd('\')) {
    throw '输出目录不符合构建规范：开发包仅 artifacts/dev，正式包仅 artifacts/release。'
}
# 拒绝输出路径祖先上的目录链接，防止通过重解析点写到仓库外。
$ancestor = $resolvedOutputRoot
while ($ancestor) {
    if ((Test-Path -LiteralPath $ancestor) -and
        ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw '输出路径包含重解析点。'
    }
    $ancestor = Split-Path -Parent $ancestor
}
New-Item -ItemType Directory -Force -Path $resolvedOutputRoot | Out-Null
$packageName = "BqtjLauncher-v$Version-win-x86"
$packageDirectory = Join-Path $resolvedOutputRoot $packageName
$archivePath = Join-Path $resolvedOutputRoot "$packageName.zip"
$checksumPath = "$archivePath.sha256"

# 已生成的交付证据不可覆盖，包括开发 ZIP；重试必须先调查残留原因。
foreach ($existing in @($packageDirectory, $archivePath, $checksumPath)) {
    if (Test-Path -LiteralPath $existing) { throw "产物已存在，拒绝覆盖：$existing" }
}
$nativeDirectory = Join-Path $resolvedOutputRoot ('.native-' + [Guid]::NewGuid().ToString('N'))
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

    if ($HotUpdate) {
        # 热更新只覆盖两个入口程序：自包含主程序会重新解压运行库，原生宿主直接替换。
        $hotUpdateNoticeName = 'HOTFIX-README.md'
        $hotUpdateNoticeSource = Join-Path $PSScriptRoot 'Publish-HotUpdate.README.md'
        # PowerShell 5.1 的 Get-Content 默认按 ANSI 读取，会把中文模板写成乱码；这里显式按 UTF-8 读。
        $hotUpdateNotice = [System.IO.File]::ReadAllText($hotUpdateNoticeSource).
            Replace('{{VERSION}}', $Version)
        # PowerShell 5.1 的 -Encoding utf8 会写出带 BOM 的 UTF-8；这里显式写无 BOM 的 UTF-8。
        [System.IO.File]::WriteAllText(
            (Join-Path $packageDirectory $hotUpdateNoticeName),
            $hotUpdateNotice,
            (New-Object System.Text.UTF8Encoding($false)))

        foreach ($releaseOnlyFile in @('README.md', 'THIRD_PARTY_NOTICES.md', 'LICENSE')) {
            $releaseOnlyPath = Join-Path $packageDirectory $releaseOnlyFile
            if (Test-Path -LiteralPath $releaseOnlyPath -PathType Leaf) {
                Remove-Item -LiteralPath $releaseOnlyPath -Force
            }
        }

        Compress-Archive `
            -Path (Join-Path $packageDirectory '*') `
            -DestinationPath $archivePath `
            -CompressionLevel Optimal
        $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        "$archiveHash  $([System.IO.Path]::GetFileName($archivePath))" |
            Set-Content -LiteralPath $checksumPath -Encoding ascii -NoNewline

        & (Join-Path $PSScriptRoot 'Test-HotUpdatePackage.ps1') `
            -ArchivePath $archivePath `
            -ChecksumPath $checksumPath
        if ($LASTEXITCODE -ne 0) {
            throw "热更新包清单验证失败，退出码：$LASTEXITCODE。"
        }

        "Created hot-update package: $archivePath"
        "Created checksum: $checksumPath"
        return
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
