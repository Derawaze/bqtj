# 中文OCR开发依赖：固定提交与SHA256，仅写开发目录，不安装或分发模型。
#Requires -Version 7.0
[CmdletBinding()]
param(
    # 离线来源目录包含清单中的四个官方文件，仍逐一校验。
    [string] $SourceDirectory,
    [switch] $VerifyOnly
)
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'maa-ocr-models.json') -Raw | ConvertFrom-Json
$repoRoot = Split-Path $PSScriptRoot -Parent
$dependencyRoot = Join-Path $repoRoot 'artifacts/dev/automation'
$destination = Join-Path $dependencyRoot $manifest.directory

# 拒绝目录链接，避免缓存准备跨出开发目录；已有组件只校验，不覆盖。
function Assert-NoReparseAncestor([string] $Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "不允许使用重解析路径：$current"
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}
function Assert-Models([string] $Directory) {
    foreach ($entry in $manifest.files.PSObject.Properties) {
        $file = Join-Path $Directory $entry.Name
        Assert-NoReparseAncestor $file
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Value) {
            throw "OCR文件缺失或校验失败：$($entry.Name)。不会加载模型。"
        }
    }
}
Assert-NoReparseAncestor $destination
if (Test-Path -LiteralPath $destination) {
    Assert-Models (Join-Path $destination 'model/ocr')
    Write-Output "PASS：OCR模型校验通过：$destination"
    return
}
if ($VerifyOnly) { throw '尚未准备固定OCR模型，请先运行Prepare-MaaOcr.ps1。' }
New-Item -ItemType Directory -Path $dependencyRoot -Force | Out-Null
$staging = Join-Path $dependencyRoot ('ocr-extract-' + [guid]::NewGuid().ToString('N'))
$modelDirectory = Join-Path $staging 'model/ocr'
New-Item -ItemType Directory -Path $modelDirectory -Force | Out-Null
foreach ($entry in $manifest.files.PSObject.Properties) {
    $file = Join-Path $modelDirectory $entry.Name
    if ($SourceDirectory) { Copy-Item -LiteralPath (Join-Path $SourceDirectory $entry.Name) -Destination $file }
    else {
        $url = $manifest.source + $manifest.commit + '/' + $manifest.path + $entry.Name
        Invoke-WebRequest -Uri $url -OutFile $file
    }
}
Assert-Models $modelDirectory
@{ commit = $manifest.commit; files = $manifest.files } | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $staging 'bqtj-ocr-source.json') -Encoding utf8
Assert-NoReparseAncestor $destination
Move-Item -LiteralPath $staging -Destination $destination
Write-Output "已准备固定中文OCR模型（SHA256通过）：$destination"
