# CI发布准备：绑定本次main提交，查询公开正式版本；只输出固定版本/状态，不打印凭据。
#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string] $Repository,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $Commit,
    [Parameter(Mandatory)][string] $OutputFile,
    [Parameter(Mandatory)][string] $Ref
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')

# 统一检查CLI退出码，网络或权限异常不能被当作“还没有Release”。
function Invoke-GitHubRead([string] $endpoint, [string[]] $options = @()) {
    $result = & gh api $endpoint @options
    if ($LASTEXITCODE -ne 0) { throw 'GitHub元数据查询失败，停止分配版本。' }
    return $result
}
function Write-Result([string] $tag, [bool] $skip) {
    @("tag=$tag", "skip=$($skip.ToString().ToLowerInvariant())") |
        Out-File -LiteralPath $OutputFile -Encoding utf8 -Append
}

if ($Ref -eq 'refs/heads/main') {
    $mainHead = (Invoke-GitHubRead "repos/$Repository/git/ref/heads/main" @('--jq', '.object.sha')).Trim()
    if ($mainHead -cne $Commit) { Write-Result '' $true; return }
    $pages = (Invoke-GitHubRead "repos/$Repository/releases" @('--paginate', '--slurp')) -join "`n" | ConvertFrom-Json
    $releases = @($pages | ForEach-Object { $_ } | ForEach-Object { $_ })
    $published = @($releases | Where-Object { -not $_.draft -and -not $_.prerelease })
    # 同一提交重跑已成功的工作流不再递增；自动发布记录完整SHA，避免main引用漂移。
    $already = @($published | Where-Object { $_.target_commitish -ceq $Commit -and $_.tag_name -cmatch '^v\d+\.\d+\.\d+$' })
    if ($already.Count -gt 0) { Write-Result $already[0].tag_name $true; return }
    $version = Get-NextReleaseVersion -PublishedTags @($published | ForEach-Object { $_.tag_name })
    $tag = "v$version"
    $refs = & git ls-remote --tags origin "refs/tags/$tag" "refs/tags/$tag^{}"
    if ($LASTEXITCODE -ne 0) { throw '远程标签检查失败。' }
    if ($refs) {
        # 仅允许重跑自身失败后留下的同提交草稿，不能覆盖其他人分配的标签。
        $draft = @($releases | Where-Object { $_.draft -and $_.tag_name -ceq $tag -and $_.target_commitish -ceq $Commit })
        $tip = @($refs | Where-Object { $_ -match '\^\{\}$' })
        if ($tip.Count -eq 0) { $tip = @($refs) }
        if ($draft.Count -ne 1 -or $tip.Count -ne 1 -or ($tip[0] -split '\s+')[0] -cne $Commit) {
            throw '下一版本标签已存在且不属于本次失败草稿，停止而非覆盖或跳号。'
        }
    }
    Write-Result $tag $false
} elseif ($Ref -cmatch '^refs/tags/v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    # 保留显式版本标签入口，较大升级可以由维护者选择MINOR/MAJOR。
    Write-Result $Ref.Substring('refs/tags/'.Length) $false
} else {
    throw '正式发布只接受main推送或严格正式版本标签。'
}
