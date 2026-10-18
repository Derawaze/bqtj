param([Parameter(Mandatory)][string] $ArchivePath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ArchivePath).Path)
try {
    foreach ($file in @($zip.Entries | Where-Object Name)) {
        if (-not $file.FullName.StartsWith('components/automation/') -or $file.FullName -match '(^|/)\.\.(/|$)' -or $file.FullName -match '(?i)(\.db|\.log|\.pfx|\.pdb)$') { throw ('组件包存在非交付内容：' + $file.FullName) }
    }
    foreach ($name in @('BqtjScriptVision.exe','component.json','LICENSE','README.md','bin/MaaFramework.dll','bin/MaaWin32ControlUnit.dll','bin/msvcp140.dll','ppocr-v4-zh-cn/model/ocr/det.onnx','ppocr-v4-zh-cn/model/ocr/rec.onnx','ppocr-v4-zh-cn/model/ocr/keys.txt')) {
        if (-not $zip.GetEntry('components/automation/' + $name)) { throw ('组件缺少：' + $name) }
    }
    $metadataReader = [IO.StreamReader]::new($zip.GetEntry('components/automation/component.json').Open())
    try { $metadata = $metadataReader.ReadToEnd() | ConvertFrom-Json } finally { $metadataReader.Dispose() }
    if ($metadata.protocolVersion -ne 2) { throw '组件必须支持协议2的存档黄字识别。' }
    $stream = $zip.GetEntry('components/automation/BqtjScriptVision.exe').Open(); $memory = [IO.MemoryStream]::new()
    try { $stream.CopyTo($memory); $reader = [IO.BinaryReader]::new($memory); $memory.Position = 0x3c; $offset = $reader.ReadInt32(); $memory.Position = $offset + 4; if ($reader.ReadUInt16() -ne 0x8664) { throw '视觉进程不是x64。' } }
    finally { $stream.Dispose(); $memory.Dispose() }
} finally { $zip.Dispose() }
$expected = (Get-Content -LiteralPath ($ArchivePath + '.sha256') -Raw).Split(' ')[0]
if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw '组件ZIP校验失败。' }
Write-Output '视觉组件路径、清单、x64和SHA256通过。'
