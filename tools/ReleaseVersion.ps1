# 正式版本规则的纯函数：只接收远程元数据，便于离线验证，不访问本机凭据或构建产物。
function Get-NextReleaseVersion {
    [CmdletBinding()]
    param(
        [AllowEmptyCollection()][string[]] $PublishedTags = @(),
        [AllowEmptyCollection()][string[]] $ReservedTags = @()
    )

    $stable = @($PublishedTags | Where-Object { $_ -cmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$' } |
        ForEach-Object { [Version]::Parse($_.Substring(1)) } | Sort-Object -Descending)
    if ($stable.Count -eq 0) { throw '没有远程正式Release基线，须先明确首个版本，禁止自动猜测。' }
    $latest = $stable[0]
    if ($latest.Build -eq [int]::MaxValue) { throw 'PATCH已超出版本范围。' }
    $next = "$($latest.Major).$($latest.Minor).$($latest.Build + 1)"
    # 已有标签但尚未正式发布可能是失败残留；不通过跳号或覆盖掩盖它。
    if ("v$next" -in $ReservedTags) { throw "下一版本标签v$next已存在，须检查草稿或失败运行。" }
    return $next
}
