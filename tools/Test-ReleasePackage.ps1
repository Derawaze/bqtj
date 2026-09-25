param(
    [Parameter(Mandatory)]
    [string] $ArchivePath,
    [string] $ChecksumPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$resolvedArchivePath = (Resolve-Path -LiteralPath $ArchivePath).Path
if ([System.IO.Path]::GetExtension($resolvedArchivePath) -ine '.zip') {
    throw '发布包清单检查只接受 ZIP 文件。'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-ZipEntryPeMachine {
    param([System.IO.Compression.ZipArchiveEntry] $Entry)

    # 只读取两个产品入口的 PE 头；不把整包解压到磁盘，避免校验阶段产生额外可执行文件。
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

$archive = [System.IO.Compression.ZipFile]::OpenRead($resolvedArchivePath)
try {
    $fileEntries = @($archive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) })
    if ($fileEntries.Count -eq 0) {
        throw '发布包为空。'
    }

    # ZIP 必须只有一个版本化根目录，且路径不能逃逸到解压目标之外。
    $normalizedPaths = @()
    foreach ($entry in $fileEntries) {
        $normalizedPath = $entry.FullName.Replace('\', '/')
        if ($normalizedPath.StartsWith('/') -or
            $normalizedPath -match '(^|/)\.\.(/|$)' -or
            $normalizedPath -match '^[A-Za-z]:') {
            throw "发布包包含不安全路径：$($entry.FullName)"
        }

        $normalizedPaths += $normalizedPath
    }

    $rootNames = @($normalizedPaths |
            ForEach-Object { ($_ -split '/', 2)[0] } |
            Sort-Object -Unique)
    if ($rootNames.Count -ne 1 -or
        $rootNames[0] -notmatch '^BqtjLauncher-v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?-win-x86$') {
        throw "发布包必须只有一个版本化 win-x86 根目录，实际：$($rootNames -join ', ')"
    }

    $rootName = $rootNames[0]
    if ([System.IO.Path]::GetFileName($resolvedArchivePath) -cne "$rootName.zip") {
        throw 'ZIP 文件名必须与包内版本化根目录一致。'
    }

    $relativePaths = @($normalizedPaths | ForEach-Object {
            $_.Substring($rootName.Length + 1)
        })
    $duplicates = @($relativePaths |
            Group-Object { $_.ToLowerInvariant() } |
            Where-Object Count -gt 1)
    if ($duplicates.Count -gt 0) {
        throw "发布包包含重复路径：$($duplicates[0].Name)"
    }

    $requiredFiles = @(
        'BqtjLauncher.Desktop.exe',
        'BqtjNativeFlashHost.exe',
        'README.md',
        'THIRD_PARTY_NOTICES.md'
    )
    if (Test-Path -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -PathType Leaf) {
        $requiredFiles += 'LICENSE'
    }

    foreach ($requiredFile in $requiredFiles) {
        if ($relativePaths -notcontains $requiredFile) {
            throw "发布包缺少必要文件：$requiredFile"
        }
    }

    # 自包含运行库及 SQLite 已嵌入主程序，正式包只允许两个入口和说明文件。
    foreach ($relativePath in $relativePaths) {
        if ($relativePath -notin $requiredFiles) { throw "精简发布包包含额外文件：$relativePath" }
    }

    foreach ($productExecutable in @('BqtjLauncher.Desktop.exe', 'BqtjNativeFlashHost.exe')) {
        $entry = $fileEntries |
            Where-Object { $_.FullName.Replace('\', '/') -ieq "$rootName/$productExecutable" } |
            Select-Object -First 1
        $machine = Get-ZipEntryPeMachine -Entry $entry
        if ($machine -ne 0x014c) {
            throw ('发布包入口必须是 x86 PE：{0}，实际 Machine=0x{1:X4}。' -f $productExecutable, $machine)
        }
    }

    $forbiddenExtensions = @(
        '.pdb', '.xml', '.dbg', '.ilk', '.lib', '.exp', '.obj',
        '.cs', '.csproj', '.sln', '.ps1', '.cmd', '.bat',
        '.zip', '.sha256', '.db', '.log', '.dmp', '.pfx', '.snk'
    )
    foreach ($relativePath in $relativePaths) {
        $extension = [System.IO.Path]::GetExtension($relativePath).ToLowerInvariant()
        if ($forbiddenExtensions -contains $extension) {
            throw "发布包包含开发、调试或敏感文件：$relativePath"
        }

        $allowedRootExtensions = @('.dll', '.exe', '.json', '.md')
        if (-not $relativePath.Contains('/') -and
            $relativePath -ine 'LICENSE' -and
            $allowedRootExtensions -notcontains $extension) {
            throw "发布包包含未声明类型的根目录文件：$relativePath"
        }

        # 诊断探针和测试项目只能参与开发验证，不能作为产品依赖进入公开包。
        if ($relativePath -match '(?i)(^|/)(Diagnostics|tests?|TestResults|bin|obj|artifacts)(/|$)' -or
            $relativePath -match '(?i)(WinInetCookieProbe|AppContainerDesktopSpike|testhost|xunit|BqtjLauncher\..*Tests)') {
            throw "发布包包含诊断或测试项目文件：$relativePath"
        }

        if ($relativePath.Contains('/')) {
            $segments = $relativePath -split '/'
            if ($segments[0] -ine 'zh-Hans' -or $relativePath -notmatch '(?i)\.resources\.dll$') {
                throw "发布包包含非必要子目录或卫星资源：$relativePath"
            }
        }
    }

    # 防止把解压后的同名目录再次打进包内，形成肉眼不易发现的重复产品树。
    if ($relativePaths | Where-Object { $_.StartsWith("$rootName/", [StringComparison]::OrdinalIgnoreCase) }) {
        throw '发布包包含嵌套的重复产品目录。'
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

$uncompressedBytes = ($fileEntries | Measure-Object -Property Length -Sum).Sum
"Verified release package: $($fileEntries.Count) files, $uncompressedBytes bytes, root=$rootName"
