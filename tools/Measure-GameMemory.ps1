param(
    [ValidateRange(1, 3600)] [int] $Samples = 30,
    [ValidateRange(1, 60)] [int] $IntervalSeconds = 2
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path

# 仅观察仓库内启动的进程，不读命令行、账号名、凭据或进程内存内容。
# PrivateMB用于观察实际私有提交量，WorkingSetMB只是当前驻留物理内存。
for ($sample = 0; $sample -lt $Samples; $sample++) {
    $rows = @(Get-Process -Name 'BqtjLauncher.Desktop', 'BqtjNativeFlashHost' -ErrorAction SilentlyContinue |
        ForEach-Object {
            try {
                if ($_.Path.StartsWith($repositoryRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
                    [pscustomobject]@{
                        Time = [DateTimeOffset]::Now.ToString('o')
                        ProcessId = $_.Id
                        Process = $_.ProcessName
                        PrivateMB = [Math]::Round($_.PrivateMemorySize64 / 1MB, 1)
                        WorkingSetMB = [Math]::Round($_.WorkingSet64 / 1MB, 1)
                        Handles = $_.HandleCount
                    }
                }
            }
            catch { }
            finally { $_.Dispose() }
        })
    if ($rows.Count) { $rows }
    else { Write-Host 'No game/launcher process running from this repository.' }
    if ($sample + 1 -lt $Samples) { Start-Sleep -Seconds $IntervalSeconds }
}
