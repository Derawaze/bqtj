param([Parameter(Mandatory)][string]$ProbePath)
$ErrorActionPreference = 'Stop'
# 父进程保持命令输入打开；生产读取线程若持有 CRT 流锁，fflush(NULL) 将超时。
$info = [Diagnostics.ProcessStartInfo]::new((Resolve-Path -LiteralPath $ProbePath).Path)
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardInput = $true
$info.RedirectStandardOutput = $true
$probe = [Diagnostics.Process]::Start($info)
try {
    if (!$probe.WaitForExit(3000)) {
        $probe.Kill()
        $probe.WaitForExit()
        Write-Output 'FAIL: CRT flush blocked by idle command pipe'
        exit 1
    }
    $output = $probe.StandardOutput.ReadToEnd().Trim()
    if ($probe.ExitCode -ne 0 -or $output -ne 'flush-completed') {
        throw "Unexpected probe result: exit=$($probe.ExitCode), output=$output"
    }
    Write-Output 'PASS: CRT flush completed while command pipe remained idle'
} finally {
    if (!$probe.HasExited) { $probe.Kill(); $probe.WaitForExit() }
    $probe.Dispose()
}
