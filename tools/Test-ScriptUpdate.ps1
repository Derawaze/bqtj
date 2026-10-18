#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string] $LauncherArchivePath,
      [Parameter(Mandatory)][string] $ComponentArchivePath)

# 只在仓库artifacts/dev/probes创建虚构安装目录；模拟文件夹合并，不触碰用户安装、账号库或日志。
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Test-ReleasePackage.ps1') -ArchivePath $LauncherArchivePath
& (Join-Path $PSScriptRoot 'Test-ScriptComponent.ps1') -ArchivePath $ComponentArchivePath
$root = Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/dev/probes/script-update-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Merge-Archive {
    param([string] $Path, [switch] $StripLauncherRoot)
    $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
    try {
        foreach ($entry in $zip.Entries | Where-Object Name) {
            $relative = $entry.FullName.Replace([char]92, [char]47)
            if ($StripLauncherRoot) { $relative = ($relative -split '/',2)[1] }
            # 上方产品校验器已检查交付清单及相对路径，本处只模拟Windows复制同名文件、保留其他文件。
            $destination = Join-Path $root $relative
            [void][IO.Directory]::CreateDirectory((Split-Path $destination -Parent))
            $inputStream = $entry.Open()
            $outputStream = [IO.File]::Create($destination)
            try { $inputStream.CopyTo($outputStream) }
            finally { $inputStream.Dispose(); $outputStream.Dispose() }
        }
    } finally { $zip.Dispose() }
}

Merge-Archive $LauncherArchivePath -StripLauncherRoot
Merge-Archive $ComponentArchivePath
$plan = Join-Path $root 'scripts/plans/10000000000000000000000000000001.json'
[void][IO.Directory]::CreateDirectory((Split-Path $plan -Parent))
[IO.File]::WriteAllText($plan,'{"version":1,"id":"10000000-0000-0000-0000-000000000001","name":"虚构更新保留方案","scriptId":"official-daily","speed":2,"concurrentAccounts":0,"tasks":[{"profileId":"20000000-0000-0000-0000-000000000001","accountName":"虚构账号","saveNumber":1,"saveName":"虚构存档","closeAfterFinish":false}]}',[Text.UTF8Encoding]::new($false))
$custom = Join-Path $root 'scripts/custom-test/script.json'
[void][IO.Directory]::CreateDirectory((Split-Path $custom -Parent))
[IO.File]::WriteAllText($custom,'{"version":1,"id":"mock-custom","name":"虚构用户脚本","entry":"wait","flow":[{"id":"wait","type":"wait","milliseconds":1}]}',[Text.UTF8Encoding]::new($false))
$plan2 = Join-Path $root 'scripts/plans/10000000000000000000000000000002.json'
[IO.File]::WriteAllText($plan2,'{"version":2,"id":"10000000-0000-0000-0000-000000000002","name":"虚构中文方案","source":"读取存档名称,虚构账号","accountBindings":{"虚构账号":"20000000-0000-0000-0000-000000000001"},"planBindings":{},"constants":{}}',[Text.UTF8Encoding]::new($false))
$catalog = Join-Path $root 'scripts/catalog/20000000000000000000000000000001.json'
[void][IO.Directory]::CreateDirectory((Split-Path $catalog -Parent))
[IO.File]::WriteAllText($catalog,'[{"number":1,"name":"虚构名称 lv.99","needsReview":false}]',[Text.UTF8Encoding]::new($false))
$globals = Join-Path $root 'scripts/config/constants.txt'
[void][IO.Directory]::CreateDirectory((Split-Path $globals -Parent))
[IO.File]::WriteAllText($globals, '日常档=存档5',[Text.UTF8Encoding]::new($false))
$marker = Join-Path $root 'scripts/plans/.built-ins-v1'
[IO.File]::WriteAllText($marker,'1',[Text.UTF8Encoding]::new($false))
$marker2 = Join-Path $root 'scripts/plans/.built-ins-v2'
[IO.File]::WriteAllText($marker2,'2',[Text.UTF8Encoding]::new($false))
$defaults = Join-Path $root 'scripts/config/account-daily-created.json'
[IO.File]::WriteAllText($defaults,'["20000000-0000-0000-0000-000000000001"]',[Text.UTF8Encoding]::new($false))
$preserved = @($plan,$plan2,$custom,$catalog,$globals,$marker,$marker2,$defaults)
$before = @($preserved | ForEach-Object { (Get-FileHash -LiteralPath $_).Hash })
Merge-Archive $LauncherArchivePath -StripLauncherRoot
Merge-Archive $ComponentArchivePath
$after = @($preserved | ForEach-Object { (Get-FileHash -LiteralPath $_).Hash })
if (($before -join ',') -ne ($after -join ',')) { throw '覆盖更新改变了用户方案或用户脚本。' }
if (!(Test-Path -LiteralPath (Join-Path $root 'BqtjLauncher.Desktop.exe')) -or
    !(Test-Path -LiteralPath (Join-Path $root 'components/automation/BqtjScriptVision.exe'))) { throw '组件没有直接落到主程序目录下的标准位置。' }
Write-Output 'PASS: 组件直接解压到EXE目录可用；两次合并更新保留v1/v2方案、全局常量、默认日常初始化记录、模板标记、名称资料及自定义脚本。'
Write-Output $root
