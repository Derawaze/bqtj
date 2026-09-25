param(
    [string] $CompilerPath,
    [switch] $AutoRestart,
    [switch] $Once
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$previewProcess = $null

# 仅关闭本监视器亲自启动的开发面板；先正常退出以释放游戏子进程，超时才终止进程树。
function Stop-OwnedPreview {
    if ($null -eq $script:previewProcess) { return }
    try {
        if (-not $script:previewProcess.HasExited) {
            $expectedRoot = Join-Path $repositoryRoot 'artifacts\dev'
            if (-not $script:previewProcess.Path.StartsWith($expectedRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Refusing to stop a process outside development output.'
            }
            [void]$script:previewProcess.CloseMainWindow()
            if (-not $script:previewProcess.WaitForExit(10000)) {
                # PowerShell 5.1的.NET Framework没有Kill(true)，使用系统进程树结束命令。
                & "$env:WINDIR\System32\taskkill.exe" /PID $script:previewProcess.Id /T /F | Out-Host
                if (-not $script:previewProcess.WaitForExit(5000)) { throw 'Development preview did not exit.' }
            }
        }
    }
    finally { $script:previewProcess.Dispose(); $script:previewProcess = $null }
}

# 只监视源码和构建配置，排除输出目录，避免构建自己触发下一轮。
function Get-SourceStamp {
    $files = @(
        foreach ($folder in @('src', 'native/FlashHost', 'tools')) {
            Get-ChildItem -LiteralPath (Join-Path $repositoryRoot $folder) -File -Recurse |
                Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
        }
        Get-ChildItem -LiteralPath $repositoryRoot -File |
            Where-Object { $_.Extension -in @('.props', '.targets', '.json') }
    )
    ($files | Sort-Object FullName | ForEach-Object {
        '{0}|{1}|{2}' -f $_.FullName, $_.Length, $_.LastWriteTimeUtc.Ticks
    }) -join "`n"
}

# 使用仓库专属命名互斥，避免两个监视器同时写入相同 bin/obj。
$hash = [Security.Cryptography.SHA256]::Create()
try {
    $key = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($repositoryRoot.ToLowerInvariant()))).Replace('-', '')
}
finally { $hash.Dispose() }
$mutex = [Threading.Mutex]::new($false, "Local\BqtjDevWatch-$key")
$held = $false
try {
    try { $held = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $held = $true }
    if (-not $held) { throw 'This repository already has a running preview watcher.' }
    $builtStamp = $null
    Write-Host "Watching source. Save to build; Ctrl+C to stop. Auto restart: $AutoRestart"
    do {
        $stamp = Get-SourceStamp
        if ($stamp -ne $builtStamp) {
            # 等待一次稳定快照，合并连续保存；失败后等下次修改再重试，避免刷屏。
            if (-not $Once) {
                Start-Sleep -Seconds 2
                if ($stamp -ne (Get-SourceStamp)) { continue }
            }
            try {
                & (Join-Path $PSScriptRoot 'Start-DevLauncher.ps1') -BuildOnly -CompilerPath $CompilerPath
                if ($AutoRestart) {
                    Stop-OwnedPreview
                    $script:previewProcess = & (Join-Path $PSScriptRoot 'Start-DevLauncher.ps1') -NoBuild -PassThru
                }
                else {
                    Write-Host 'Preview ready. Run tools/Start-DevLauncher.ps1 -NoBuild.'
                }
            }
            catch {
                if ($Once) { throw }
                Write-Warning $_.Exception.Message
                Write-Host 'Fix/save a source file to retry, or restart this watcher.'
            }
            $builtStamp = $stamp
        }
        if (-not $Once) { Start-Sleep -Seconds 2 }
    } while (-not $Once)
}
finally {
    # 停止监视不关闭游戏，用户可继续验收；只释放脚本持有的进程句柄。
    if ($null -ne $previewProcess) { $previewProcess.Dispose() }
    if ($held) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
