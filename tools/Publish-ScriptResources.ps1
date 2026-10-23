#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^0\.0\.0-dev\.\d{14}$')][string] $Version)

# 只打包官方脚本与识别资源，直接合并到EXE目录；不收集任何运行目录中的用户方案。
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$outputRoot = Join-Path $repositoryRoot 'artifacts/dev'
[void][IO.Directory]::CreateDirectory($outputRoot)
$archivePath = Join-Path $outputRoot ("BqtjScripts-v$Version.zip")
if ((Test-Path -LiteralPath $archivePath) -or (Test-Path -LiteralPath ($archivePath + '.sha256'))) { throw '脚本包已存在，拒绝覆盖。' }
Add-Type -AssemblyName System.IO.Compression
$stream = [IO.File]::Open($archivePath, [IO.FileMode]::CreateNew)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($source in @(@{ Directory = 'resources/scripts'; Target = 'scripts' }, @{ Directory = 'resources/automation/sky-top'; Target = 'scripts/resources/loot' })) {
        $root = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $source.Directory))
        foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse) {
            $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace([char]92, [char]47)
            $entry = $archive.CreateEntry(($source.Target + '/' + $relative), [IO.Compression.CompressionLevel]::Optimal)
            $inputStream = $file.OpenRead(); $outputStream = $entry.Open()
            try { $inputStream.CopyTo($outputStream) }
            finally { $inputStream.Dispose(); $outputStream.Dispose() }
        }
    }
} finally { $archive.Dispose(); $stream.Dispose() }
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($archivePath + '.sha256'), ($hash + ' *' + [IO.Path]::GetFileName($archivePath) + [char]10), [Text.UTF8Encoding]::new($false))
Write-Output $archivePath
