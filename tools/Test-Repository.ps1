#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $BaseRevision,
    [switch] $FullVerification,
    [string] $GitHubOutput
)

# 快速仓库门禁：只读Git清单、Markdown及变更路径，不加载运行目录或用户数据。
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$files = @(git -C $repositoryRoot -c core.quotepath=false ls-files)
if ($LASTEXITCODE -ne 0) { throw '无法读取Git文件清单。' }
$forbiddenPattern = '(^|/)(artifacts|bin|obj|TestResults|coverage|logs|data|profiles|\.local|\.nuget)(/|$)|\.(exe|dll|pdb|zip|db|db-shm|db-wal|sqlite|sqlite3|sqlite-shm|sqlite-wal|dmp|etl|trace|log|jsonl|pfx|p12|snk|pem|key)$|(^|/)\.env(\..+)?$|\.secrets\.json$'
$forbidden = @($files | Where-Object { $_ -match $forbiddenPattern -and $_ -ne '.env.example' })
if ($forbidden.Count) { throw ('不应提交的生成或私有文件：' + ($forbidden -join ', ')) }

# 只检查仓库内的相对链接；不联网检测外链，也不要求本机生成产物存在。
$links = 0
foreach ($relative in $files | Where-Object { $_ -like '*.md' }) {
    $path = Join-Path $repositoryRoot $relative
    if (-not (Test-Path -LiteralPath $path)) { continue }
    $content = Get-Content -LiteralPath $path -Raw
    $content = [regex]::Replace($content, '(?ms)^\s*```[^\n]*\n.*?^\s*```[^\n]*$', '')
    foreach ($match in [regex]::Matches($content, '\[[^\]\r\n]+\]\((<[^>]+>|[^)\s]+)\s*(?:"[^"]*")?\)')) {
        $target = $match.Groups[1].Value.Trim('<', '>')
        if ($target -match '^(?:[a-z][a-z0-9+.-]*:|#)') { continue }
        $local = [Uri]::UnescapeDataString(($target -split '[#?]', 2)[0])
        if ($local -and -not (Test-Path -LiteralPath (Join-Path (Split-Path $path -Parent) $local))) {
            throw "文档链接失效：$relative -> $target"
        }
        $links++
    }
}

# 新分支/手动运行走完整验证；文档改动仍经过上方门禁，跳过昂贵的Windows构建。
$all = $FullVerification -or -not $BaseRevision -or $BaseRevision -match '^0+$'
$changed = if ($all) { $files } else {
    @(git -C $repositoryRoot diff --name-only $BaseRevision HEAD)
    if ($LASTEXITCODE -ne 0) { throw '无法计算CI变更范围。' }
}
$product = $all -or @($changed | Where-Object {
    ($_ -match '^(src|tests|native|resources|tools)/' -and $_ -notmatch '\.md$') -or
    $_ -match '^\.github/workflows/|^BqtjLauncher\.sln$|^Directory\.Build\.|^global\.json$|^\.editorconfig$|^\.gitattributes$'
}).Count -gt 0
$workflow = $all -or @($changed | Where-Object { $_ -match '^\.github/workflows/' }).Count -gt 0
if ($GitHubOutput) {
    "product=$($product.ToString().ToLowerInvariant())" | Add-Content -LiteralPath $GitHubOutput -Encoding utf8
    "workflow=$($workflow.ToString().ToLowerInvariant())" | Add-Content -LiteralPath $GitHubOutput -Encoding utf8
}
Write-Output "仓库文件与${links}个文档链接通过；Windows验证=$product，工作流校验=$workflow。"
