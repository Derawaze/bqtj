param(
    [Parameter(Mandatory)]
    [string] $ArchivePath,
    [string] $ChecksumPath
)

$ErrorActionPreference = 'Stop'
$resolvedArchivePath = (Resolve-Path -LiteralPath $ArchivePath).Path
if ([System.IO.Path]::GetExtension($resolvedArchivePath) -ine '.zip') {
    throw '热更新包检查只接受 ZIP 文件。'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-ZipEntryPeMachine {
    param([System.IO.Compression.ZipArchiveEntry] $Entry)

    # 只读取两个入口的 PE 头，不把整包解压到磁盘。
    $entryStream = $Entry.Open()
    $memoryStream = [System.IO.MemoryStream]::new()
    try {
        $entryStream.CopyTo($memoryStream)
        $reader = [System.IO.BinaryReader]::new($memoryStream)
        $memoryStream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        $memoryStream.Position = $peOffset + 4
        return $reader.ReadUInt16()
    }
    finally {
        $entryStream.Dispose()
        $memoryStream.Dispose()
    }
}

# 热更新包是扁平结构：两个入口程序加一份修复说明，全部覆盖到安装目录。
$allowedFiles = @(
    'BqtjLauncher.Desktop.exe',
    'BqtjNativeFlashHost.exe',
    'HOTFIX-README.md'
)

$archive = [System.IO.Compression.ZipFile]::OpenRead($resolvedArchivePath)
try {
    $entries = @($archive.Entries)
    $fileEntries = @($entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) })
    if ($fileEntries.Count -eq 0) {
        throw '热更新包为空。'
    }

    $relativePaths = @()
    foreach ($entry in $fileEntries) {
        $normalizedPath = $entry.FullName.Replace('\', '/')
        if ($normalizedPath.StartsWith('/') -or
            $normalizedPath -match '(^|/)\.\.(/|$)' -or
            $normalizedPath -match '^[A-Za-z]:' -or
            $normalizedPath.Contains('/')) {
            throw "热更新包必须是无子目录的扁平结构：$($entry.FullName)"
        }

        $relativePaths += $normalizedPath
    }

    $duplicates = @($relativePaths |
            Group-Object { $_.ToLowerInvariant() } |
            Where-Object Count -gt 1)
    if ($duplicates.Count -gt 0) {
        throw "热更新包包含重复文件：$($duplicates[0].Name)"
    }

    foreach ($requiredFile in $allowedFiles) {
        if ($relativePaths -notcontains $requiredFile) {
            throw "热更新包缺少必要文件：$requiredFile"
        }
    }

    foreach ($relativePath in $relativePaths) {
        if ($relativePath -notin $allowedFiles) {
            throw "热更新包包含未声明文件：$relativePath"
        }
    }

    # 说明文件必须已经替换版本号占位符，避免交付一个写着 {{VERSION}} 的说明。
    $noticeEntry = $fileEntries |
        Where-Object { $_.FullName -ieq 'HOTFIX-README.md' } |
        Select-Object -First 1
    $noticeStream = $noticeEntry.Open()
    try {
        $noticeReader = [System.IO.StreamReader]::new($noticeStream, [System.Text.Encoding]::UTF8)
        $noticeText = $noticeReader.ReadToEnd()
    }
    finally {
        $noticeStream.Dispose()
    }
    if ($noticeText -match '\{\{VERSION\}\}') {
        throw '热更新说明未替换版本号占位符。'
    }

    foreach ($productExecutable in @('BqtjLauncher.Desktop.exe', 'BqtjNativeFlashHost.exe')) {
        $entry = $fileEntries |
            Where-Object { $_.FullName -ieq $productExecutable } |
            Select-Object -First 1
        $machine = Get-ZipEntryPeMachine -Entry $entry
        if ($machine -ne 0x014c) {
            throw ('热更新入口必须是 x86 PE：{0}，实际 Machine=0x{1:X4}。' -f $productExecutable, $machine)
        }
    }
}
finally {
    $archive.Dispose()
}

if (-not [string]::IsNullOrWhiteSpace($ChecksumPath)) {
    $resolvedChecksumPath = (Resolve-Path -LiteralPath $ChecksumPath).Path
    $checksumLine = (Get-Content -LiteralPath $resolvedChecksumPath -Raw).Trim()
    $expectedFileName = [System.IO.Path]::GetFileName($resolvedArchivePath)
    if ($checksumLine -notmatch '^([0-9A-Fa-f]{64})  (.+)$' -or $Matches[2] -cne $expectedFileName) {
        throw 'SHA-256 文件格式或目标文件名不正确。'
    }

    $actualHash = (Get-FileHash -LiteralPath $resolvedArchivePath -Algorithm SHA256).Hash
    if ($actualHash -ine $Matches[1]) {
        throw 'SHA-256 校验失败。'
    }
}

"Verified hot-update package: $($fileEntries.Count) files, root=flat"
