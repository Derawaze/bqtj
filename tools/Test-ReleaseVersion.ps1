# 正式版本离线回归：使用虚构标签，不创建标签、构建包或调用GitHub。
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')
$passed = 0
function Assert-Version([string[]] $tags, [string] $expected) {
    $actual = Get-NextReleaseVersion -PublishedTags $tags
    if ($actual -cne $expected) { throw "版本计算失败：预期$expected，得到$actual。" }
    $script:passed++
}
function Assert-Rejected([scriptblock] $operation) {
    $rejected = $false
    try { & $operation | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw '应拒绝的版本输入被接受。' }
    $script:passed++
}
Assert-Version @('v0.1.4') '0.1.5'
Assert-Version @('v0.1.9', 'v0.1.10', 'v0.1.2') '0.1.11'
Assert-Version @('v1.9.99', 'v2.0.0', 'v0.99.99') '2.0.1'
Assert-Version @('v0.1.4', 'v9.0.0-rc.1', 'dev', 'v01.2.3') '0.1.5'
Assert-Rejected { Get-NextReleaseVersion -PublishedTags @() }
Assert-Rejected { Get-NextReleaseVersion -PublishedTags @('v0.1.4') -ReservedTags @('v0.1.5') }
Assert-Rejected { Get-NextReleaseVersion -PublishedTags @('v0.1.2147483647') }
Write-Output "PASS: $passed 项正式版本回归，未访问网络或生成正式包。"
