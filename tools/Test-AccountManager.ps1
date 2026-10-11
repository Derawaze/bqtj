param(
    [Parameter(Mandatory)]
    [string] $CompilerPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repositoryRoot ('artifacts/dev/probes/account-manager-ui-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))
$probeBin = Join-Path $repositoryRoot 'artifacts/dev/probes/account manager ui'
$scope = 'Local\BqtjLauncher.AccountManager.Probe.' + [Guid]::NewGuid().ToString('N')
[IO.Directory]::CreateDirectory($output) | Out-Null

# 独立测试命名空间，不激活用户正在运行的生产工具，也不访问真实数据库。
$wrapper = '#define BQTJ_ACCOUNT_EVENT_NAME L"' + $scope.Replace('\', '\\') + '.Activate"' + [Environment]::NewLine +
    '#include "' + (Join-Path $repositoryRoot 'native/AccountTool/account_tool_launcher.c').Replace('\', '/') + '"' + [Environment]::NewLine
$wrapperPath = Join-Path $output 'account_entry_fixture.c'
[IO.File]::WriteAllText($wrapperPath, $wrapper, [Text.UTF8Encoding]::new($false))

dotnet build (Join-Path $repositoryRoot 'native/Diagnostics/AccountTransferUiProbe/AccountTransferUiProbe.csproj') -c Release -o $probeBin -v quiet
if ($LASTEXITCODE -ne 0) { throw '账号管理窗口探针构建失败。' }

$environmentNames = @('PATH', 'BQTJ_ACCOUNT_UI_ROOT', 'BQTJ_ACCOUNT_UI_OUTPUT', 'BQTJ_ACCOUNT_UI_SCOPE', 'BQTJ_ACCOUNT_UI_HELPER')
$previous = @{}
foreach ($name in $environmentNames) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:PATH = (Split-Path -Parent $CompilerPath) + ';' + $env:PATH
    $helper = Join-Path $probeBin 'BqtjAccountTool.exe'
    & $CompilerPath -std=c11 -Os -s -Wall -Wextra -municode -mwindows -o $helper $wrapperPath -lshell32
    if ($LASTEXITCODE -ne 0) { throw '账号管理原生入口探针构建失败。' }
    $env:BQTJ_ACCOUNT_UI_ROOT = $repositoryRoot
    $env:BQTJ_ACCOUNT_UI_OUTPUT = $output
    $env:BQTJ_ACCOUNT_UI_SCOPE = $scope
    $env:BQTJ_ACCOUNT_UI_HELPER = $helper
    $entry = Start-Process -FilePath $helper -WindowStyle Hidden -Wait -PassThru
    if ($entry.ExitCode -ne 0) { throw '账号管理独立入口启动失败。' }
    $deadline = [DateTime]::UtcNow.AddSeconds(50)
    $resultPath = Join-Path $output 'result.txt'
    while (-not (Test-Path -LiteralPath $resultPath) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path -LiteralPath $resultPath)) { throw ('账号管理探针超时，证据：' + $output) }
    $result = [IO.File]::ReadAllText($resultPath)
    $result
    'UI evidence: ' + $output
    if (-not $result.StartsWith('PASS:')) { throw '账号管理实际窗口验证失败。' }
}
finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
