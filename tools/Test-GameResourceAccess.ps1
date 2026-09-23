param(
    [Parameter(Mandatory = $true)]
    [uri] $GamePageUri,

    [int] $MinimumSwfBytes = 1000000
)

$ErrorActionPreference = 'Stop'
$headers = @{ 'User-Agent' = 'Mozilla/5.0' }
$pageResponse = Invoke-WebRequest -Uri $GamePageUri -Headers $headers -UseBasicParsing
$html = [string] $pageResponse.Content

$movieMatch = [regex]::Match(
    $html,
    '<param\s+name=["'']movie["'']\s+value=["''](?<movie>[^"'']+\.swf)["'']',
    [Text.RegularExpressions.RegexOptions]::IgnoreCase)

if (-not $movieMatch.Success) {
    throw "FAIL: $GamePageUri did not return a Flash game wrapper."
}

$movieUri = [uri]::new($GamePageUri, $movieMatch.Groups['movie'].Value)
$swfResponse = Invoke-WebRequest -Uri $movieUri -Headers $headers -UseBasicParsing
$bytes = if ($swfResponse.Content -is [string]) {
    [Text.Encoding]::UTF8.GetBytes($swfResponse.Content)
} else {
    [byte[]] $swfResponse.Content
}

$magic = [Text.Encoding]::ASCII.GetString($bytes, 0, [Math]::Min(3, $bytes.Length))
if ($magic -notin @('FWS', 'CWS', 'ZWS')) {
    throw "FAIL: $movieUri returned magic '$magic', not a SWF."
}

if ($bytes.Length -lt $MinimumSwfBytes) {
    throw "FAIL: $movieUri returned only $($bytes.Length) bytes; this is the access-error SWF."
}

"PASS page=$GamePageUri movie=$movieUri bytes=$($bytes.Length) magic=$magic"
