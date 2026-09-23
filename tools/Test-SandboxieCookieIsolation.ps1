param(
    [Parameter(Mandatory = $true)]
    [string] $ProbePath,

    [Parameter(Mandatory = $true)]
    [string] $SandboxieStartPath
)

# 只运行两个专用诊断沙箱；不安装沙箱、不改系统配置、不复制真实登录态。
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$probe = (Resolve-Path -LiteralPath $ProbePath).Path
$sandboxStart = (Resolve-Path -LiteralPath $SandboxieStartPath).Path
if ([IO.Path]::GetFileName($probe) -ne 'WinInetCookieProbe.exe' -or
    [IO.Path]::GetFileName($sandboxStart) -ne 'Start.exe') {
    throw '需要 WinInetCookieProbe.exe 与 Sandboxie 安装目录中的 Start.exe。'
}
if ((Get-Service -Name SbieSvc).Status -ne 'Running') {
    throw 'Sandboxie 服务未运行，停止探针；不回退普通进程。'
}

$url = 'http://' + [guid]::NewGuid().ToString('N') + '.bqtj-isolation.invalid/'
$observations = [Collections.Generic.List[object]]::new()
$probeFailed = $false

function Invoke-CookieProbe([string] $Account, [string] $Operation, [switch] $Outside) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($probe)
    if ($Outside) {
        $start.FileName = $probe
        $arguments = @('matrix-child', 'shared', $url, $Account, $Operation)
    }
    else {
        $start.FileName = $sandboxStart
        $box = "BqtjCookieProbe$Account"
        # /wait 返回被启动程序的退出码；子进程还会独立校验实际沙箱名称。
        $arguments = @("/box:$box", '/silent', '/wait', '/hide_window', $probe,
            'matrix-child', 'sandboxie', $url, $Account, $Operation, $box)
    }
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit(20000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "探针超时：$Account/$Operation；需检查专用诊断沙箱有无残留进程。"
        }
        $code = $process.ExitCode
        if ($code -notin @(0, 10, 11)) {
            throw "探针执行失败：$Account/$Operation，退出码 $code。"
        }
        return $code
    }
    finally { $process.Dispose() }
}

function Assert-CookieObservation([string] $Account, [string] $Operation, [int] $Expected, [switch] $Outside) {
    $actual = Invoke-CookieProbe $Account $Operation -Outside:$Outside
    $observations.Add([pscustomobject]@{
        account = $Account; operation = $Operation; outside = [bool]$Outside
        expected = $Expected; actual = $actual
    })
    if ($actual -ne $Expected) {
        throw "隔离门禁失败：$Account/$Operation，预期 $Expected，实际 $actual。"
    }
}

try {
    # 宿主先写入合成值，两箱必须看不见；检出沙箱默认读穿透导致的旧登录态继承。
    Assert-CookieObservation A set 0 -Outside
    Assert-CookieObservation A has 10
    Assert-CookieObservation B has 10
    Assert-CookieObservation A clear 0 -Outside
    Assert-CookieObservation A set 0
    Assert-CookieObservation B has 10
    Assert-CookieObservation A matches 0
    Assert-CookieObservation B set 0
    Assert-CookieObservation A matches 0
    Assert-CookieObservation B matches 0
    Assert-CookieObservation A has 10 -Outside
    # 清除 A 不影响 B；每个调用都启动新进程，同时检验重启持久化。
    Assert-CookieObservation A clear 0
    Assert-CookieObservation A has 10
    Assert-CookieObservation B matches 0
}
catch {
    $probeFailed = $true
    Write-Warning $_.Exception.Message
}
finally {
    foreach ($account in @('A', 'B')) {
        try {
            Assert-CookieObservation $account clear 0
            Assert-CookieObservation $account has 10
        }
        catch {
            $probeFailed = $true
            Write-Warning "本轮合成 Cookie 清理未确认：$account。"
        }
    }
    try {
        Assert-CookieObservation A clear 0 -Outside
        Assert-CookieObservation A has 10 -Outside
    }
    catch {
        $probeFailed = $true
        Write-Warning '宿主合成 Cookie 清理未确认。'
    }
}

[pscustomobject]@{
    mode = 'sandboxie'
    passed = -not $probeFailed
    scope = 'synthetic-cookie-only'
    observations = $observations.ToArray()
} | ConvertTo-Json -Depth 4
if ($probeFailed) { exit 1 }
