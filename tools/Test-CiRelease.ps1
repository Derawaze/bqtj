# CI准备脚本离线回归：替代gh/git读取边界，验证重跑、草稿、标签冲突与网络失败。
#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$passed = 0
$commit = 'a' * 40
$otherCommit = 'b' * 40
$resultPath = [IO.Path]::GetTempFileName()

function gh {
    param([Parameter(ValueFromRemainingArguments)][string[]] $Arguments)
    $global:LASTEXITCODE = 0
    if ($global:BqtjReleaseFixture.FailRead) { $global:LASTEXITCODE = 1; return }
    if ($Arguments[1] -like '*/git/ref/heads/main') { return $global:BqtjReleaseFixture.MainHead }
    if ($Arguments[1] -like '*/releases') { return $global:BqtjReleaseFixture.Pages }
    throw '出现未定义的GitHub查询。'
}
function git {
    param([Parameter(ValueFromRemainingArguments)][string[]] $Arguments)
    $global:LASTEXITCODE = 0
    if ($Arguments[0] -eq 'ls-remote') { return $global:BqtjReleaseFixture.Refs }
    throw '出现未定义的Git查询。'
}
function Release([string] $tag, [bool] $draft = $false, [string] $target = $otherCommit) {
    return @{ tag_name = $tag; draft = $draft; prerelease = $false; target_commitish = $target }
}
function Check([object[]] $releases, [string] $expected, [string[]] $refs = @(),
    [string] $head = $commit, [bool] $failRead = $false, [string] $ref = 'refs/heads/main', [bool] $reject = $false) {
    $global:BqtjReleaseFixture = @{
        MainHead = $head; FailRead = $failRead; Refs = $refs
        Pages = '[' + (ConvertTo-Json -InputObject @($releases) -Depth 5 -Compress) + ']'
    }
    [IO.File]::WriteAllText($resultPath, '')
    $failure = $false
    try {
        & (Join-Path $PSScriptRoot 'Resolve-CiRelease.ps1') -Repository 'example/demo' -Commit $commit -OutputFile $resultPath -Ref $ref
    } catch { $failure = $true }
    if ($failure -ne $reject) { throw 'CI发布准备的拒绝状态不符合预期。' }
    if (-not $reject) {
        $actual = [IO.File]::ReadAllText($resultPath).Replace("`r`n", "`n").Trim()
        if ($actual -cne $expected) { throw "CI发布输出不符合预期：$actual" }
    }
    $script:passed++
}
try {
    Check @((Release 'v0.1.4')) "tag=v0.1.5`nskip=false"
    Check @((Release 'v0.1.9'), (Release 'v0.1.10')) "tag=v0.1.11`nskip=false"
    Check @((Release 'v0.1.4')) "tag=`nskip=true" -head $otherCommit
    Check @((Release 'v0.1.5' -target $commit)) "tag=v0.1.5`nskip=true"
    Check @((Release 'v0.1.4'), (Release 'v0.1.5' $true $commit)) "tag=v0.1.5`nskip=false" -refs @("$commit`trefs/tags/v0.1.5")
    Check @((Release 'v0.1.4'), (Release 'v0.1.5' $true $otherCommit)) '' -refs @("$otherCommit`trefs/tags/v0.1.5") -reject $true
    Check @((Release 'v0.1.4')) '' -refs @("$commit`trefs/tags/v0.1.5") -reject $true
    Check @((Release 'v0.1.4')) '' -failRead $true -reject $true
    Check @() '' -reject $true
    Check @() "tag=v0.2.0`nskip=false" -ref 'refs/tags/v0.2.0'
    Check @() '' -ref 'refs/tags/v0.2.0-rc.1' -reject $true
    Write-Output "PASS: $passed 项CI发布准备回归；未访问网络、创建标签或发布。"
} finally {
    # 只移除本次创建的空白临时输出文件；不扫描用户目录。
    Remove-Item -LiteralPath $resultPath -Force
    Remove-Item Function:gh, Function:git
    Remove-Variable BqtjReleaseFixture -Scope Global -ErrorAction SilentlyContinue
}
