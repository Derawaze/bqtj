#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^0\.0\.0-dev\.\d{14}$')][string] $Version,
      [string] $Compiler64Path, [string] $VcRuntimeDirectory)

# 独立组件以components/automation为覆盖路径；不覆盖已有开发交付证据。
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$developmentRoot = Join-Path $repositoryRoot 'artifacts/dev'
$name = "BqtjScriptComponent-v$Version-win-x64"
$package = Join-Path $developmentRoot $name
$archive = Join-Path $developmentRoot ($name + '.zip')
foreach ($path in @($package,$archive,($archive + '.sha256'))) { if (Test-Path -LiteralPath $path) { throw ('产物已存在：' + $path) } }
& (Join-Path $PSScriptRoot 'Build-ScriptComponent.ps1') -OutputDirectory (Join-Path $package 'components/automation') -Compiler64Path $Compiler64Path -VcRuntimeDirectory $VcRuntimeDirectory
Compress-Archive -Path (Join-Path $package 'components') -DestinationPath $archive -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($archive + '.sha256'), ($hash + ' *' + [IO.Path]::GetFileName($archive) + [char]10), [Text.UTF8Encoding]::new($false))
& (Join-Path $PSScriptRoot 'Test-ScriptComponent.ps1') -ArchivePath $archive
Write-Output $archive
