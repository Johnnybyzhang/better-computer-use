param([string]$ReleaseMetadata = (Join-Path $PSScriptRoot '../artifacts/latest-release.json'), [switch]$SkipExecution)
$ErrorActionPreference = 'Stop'
$release = Get-Content -LiteralPath $ReleaseMetadata -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $release.Archive -Algorithm SHA256).Hash -ne $release.SHA256) { throw 'Archive checksum mismatch.' }
$destination = Join-Path ([IO.Path]::GetDirectoryName($release.Archive)) ('verify-' + [guid]::NewGuid().ToString('N'))
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($release.Archive, $destination)
# Invoke in a child shell because Install.ps1 intentionally exits in VerifyOnly mode.
$shellExe = (Get-Process -Id $PID).Path
& $shellExe -NoProfile -File (Join-Path $destination 'Install.ps1') -VerifyOnly
if ($LASTEXITCODE -ne 0) { throw 'Extracted bundle verification failed.' }
$manifest = Get-Content -LiteralPath (Join-Path $destination 'SHA256SUMS.json') -Raw | ConvertFrom-Json
$exe = Join-Path $destination 'better-computer-use/bin/BetterComputerUse.exe'
$stream = [IO.File]::OpenRead($exe)
$reader = New-Object IO.BinaryReader($stream)
try {
    $stream.Position = 0x3c
    $offset = $reader.ReadInt32()
    $stream.Position = $offset
    if ($reader.ReadUInt32() -ne 0x4550) { throw 'Invalid executable PE signature.' }
    $machine = $reader.ReadUInt16()
    $expected = if ($manifest.runtime -eq 'win-arm64') { 0xaa64 } else { 0x8664 }
    if ($machine -ne $expected) { throw 'Executable architecture does not match the bundle manifest.' }
} finally { $reader.Dispose(); $stream.Dispose() }
if (-not $SkipExecution) {
    & (Join-Path $destination 'better-computer-use/bin/BetterComputerUse.exe') --help
    if ($LASTEXITCODE -ne 0) { throw 'Native executable smoke test failed.' }
}
Write-Host "Verified $($release.Archive)"
