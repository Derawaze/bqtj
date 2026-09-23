param([string] $KeepVersion = '0.1.0')
$ErrorActionPreference = 'Stop'
if ($KeepVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw '版本号无效。' }
$root = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$artifacts = Join-Path $root 'artifacts'
$keep = "BqtjLauncher-v$KeepVersion-win-x86"
$targets = @()
if (Test-Path -LiteralPath $artifacts) {
    foreach ($item in Get-ChildItem -LiteralPath $artifacts -Force) {
        if ($item.Name -eq 'release' -and $item.PSIsContainer) {
            $targets += Get-ChildItem -LiteralPath $item.FullName -Force |
                Where-Object Name -notin @($keep, "$keep.zip", "$keep.zip.sha256")
        } else { $targets += $item }
    }
}
foreach ($sourceRoot in @('src','tests','native')) {
    $targets += Get-ChildItem -LiteralPath (Join-Path $root $sourceRoot) -Directory -Recurse |
        Where-Object Name -in @('bin','obj')
}
$running = @(Get-Process | ForEach-Object { try { $_.Path } catch {} } | Where-Object { $_ })
[long]$removedBytes = 0
$removedCount = 0
foreach ($item in $targets) {
    # 每个删除目标都重新解析；仅清理仓库生成目录，拒绝重解析点、运行中文件及用户数据。
    if (-not (Test-Path -LiteralPath $item.FullName)) { continue }
    $path = (Resolve-Path -LiteralPath $item.FullName).Path
    if (-not $path.StartsWith("$root\", [StringComparison]::OrdinalIgnoreCase)) { throw '清理目标越出仓库。' }
    if (-not ($path.StartsWith("$artifacts\", [StringComparison]::OrdinalIgnoreCase) -or
        ($item.Name -in @('bin','obj') -and $item.PSIsContainer))) { throw '非生成目录被拒绝。' }
    $children = if ($item.PSIsContainer) { @(Get-ChildItem -LiteralPath $path -Recurse -Force) } else { @() }
    $all = @($item) + $children
    if ($all | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { Write-Warning "跳过重解析点：$path"; continue }
    if ($running | Where-Object { $_ -eq $path -or $_.StartsWith("$path\", [StringComparison]::OrdinalIgnoreCase) }) { Write-Warning "跳过运行中的产物：$path"; continue }
    if ($all | Where-Object { -not $_.PSIsContainer -and $_.Extension -in @('.db','.pfx','.snk') }) { Write-Warning "跳过含用户数据的目录：$path"; continue }
    $bytes = ($all | Where-Object { -not $_.PSIsContainer } | Measure-Object Length -Sum).Sum
    Remove-Item -LiteralPath $path -Recurse -Force
    $removedBytes += $bytes
    $removedCount++
}
[pscustomobject]@{ RemovedTargets=$removedCount; RemovedBytes=$removedBytes; RemovedMiB=[Math]::Round($removedBytes/1MB,2); KeptRelease=$keep }
