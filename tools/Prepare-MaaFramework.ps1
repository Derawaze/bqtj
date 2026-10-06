# 开发依赖准备：固定官方资产和SHA256，不安装到系统，也不改动启动器发行包。
#Requires -Version 7.0
[CmdletBinding()]
param(
    # 可使用提前下载的官方ZIP；仍执行同一校验，不接受任意版本替换。
    [string] $ArchivePath
)

$ErrorActionPreference = 'Stop'
$version = '5.14.2'
$assetName = "MAA-win-x86_64-v$version.zip"
$expectedHash = 'e279163bcdcab7c87ebe5a9dd157c47e169c78742dbdcc30d095de55ab0eaa9c'
$assetUrl = "https://github.com/MaaXYZ/MaaFramework/releases/download/v$version/$assetName"
$repoRoot = Split-Path $PSScriptRoot -Parent
$dependencyRoot = Join-Path $repoRoot 'artifacts/dev/automation'
$destination = Join-Path $dependencyRoot "maa-$version-win-x64"

# 所有写入固定在开发目录，并拒绝经目录链接写到其他位置。
function Assert-NoReparseAncestor([string] $Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "不允许使用重解析路径：$current"
            }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

Assert-NoReparseAncestor $destination
New-Item -ItemType Directory -Path $dependencyRoot -Force | Out-Null
if ($ArchivePath) {
    $archive = (Resolve-Path -LiteralPath $ArchivePath).Path
}
else {
    $archive = Join-Path $dependencyRoot $assetName
    Assert-NoReparseAncestor $archive
    if (-not (Test-Path -LiteralPath $archive)) {
        # 下载失败保留独立临时文件，绝不将未完成ZIP当作缓存。
        $partial = Join-Path $dependencyRoot ("download-" + [guid]::NewGuid().ToString('N') + '.partial')
        Invoke-WebRequest -Uri $assetUrl -OutFile $partial
        if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $expectedHash) {
            throw "官方资产SHA256不符；未解压。请检查：$partial"
        }
        Move-Item -LiteralPath $partial -Destination $archive
    }
}

if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) {
    throw '组件ZIP校验失败，未解压或加载任何DLL。请使用v5.14.2官方Windows x64资产。'
}

if (Test-Path -LiteralPath $destination) {
    # 不覆盖已经解压的目录，避免运行期间替换DLL；加载能力另用Test-MaaRuntime验证。
    $marker = Join-Path $destination 'bqtj-dependency.json'
    if (-not (Test-Path -LiteralPath $marker)) {
        throw "目标目录存在但没有准备完成标记，请检查后使用新的开发目录：$destination"
    }
    Assert-NoReparseAncestor $marker
    $existing = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
    if ($existing.version -ne $version -or $existing.sha256 -ne $expectedHash) {
        throw '已存在组件的版本记录不符；不会覆盖。'
    }
    Write-Output "组件已准备（ZIP校验通过，未覆盖已解压文件）：$destination"
    return
}

# 先完整解压到本次专用目录，成功后移动；失败目录供排查，不执行自动递归删除。
$staging = Join-Path $dependencyRoot ("extract-" + [guid]::NewGuid().ToString('N'))
[IO.Compression.ZipFile]::ExtractToDirectory($archive, $staging)
$libraries = @(Get-ChildItem -LiteralPath $staging -Filter 'MaaFramework.dll' -Recurse -File)
if ($libraries.Count -ne 1) {
    throw '官方资产中未找到唯一MaaFramework.dll，组件未启用。'
}
@{
    version = $version
    sha256 = $expectedHash
    source = $assetUrl
    preparedAtUtc = [DateTime]::UtcNow.ToString('o')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $staging 'bqtj-dependency.json') -Encoding utf8

$resolvedStaging = (Resolve-Path -LiteralPath $staging).Path
$allowedPrefix = [IO.Path]::GetFullPath($dependencyRoot) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedStaging.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not ([IO.Path]::GetFullPath($destination)).StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw '解压源或目标超出开发目录，停止移动。'
}
Assert-NoReparseAncestor $destination
Move-Item -LiteralPath $resolvedStaging -Destination $destination
Write-Output "已准备MaaFramework $version（SHA256已验证）：$destination"
