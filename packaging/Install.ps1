param([switch]$VerifyOnly, [string]$CodexPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$bundleRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$pluginName = 'better-computer-use'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Assert-Within([string]$Path, [string]$Root) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Path outside intended directory: $fullPath" }
    return $fullPath
}

Write-Host 'Verifying bundle checksums...'
$manifestPath = Join-Path $bundleRoot 'SHA256SUMS.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$expected = @{}
foreach ($entry in $manifest.files) {
    $filePath = Assert-Within (Join-Path $bundleRoot $entry.path) $bundleRoot
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) { throw "Missing bundle file: $($entry.path)" }
    if ((Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Checksum mismatch: $($entry.path)" }
    $expected[$filePath] = $true
}
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $bundleRoot 'better-computer-use') -Recurse -Force -File) {
    if (-not $expected.ContainsKey($file.FullName)) { throw "Unexpected plugin file: $($file.FullName)" }
}
if ($VerifyOnly) { Write-Host 'Bundle checksums verified. Nothing installed.'; exit 0 }
if ($manifest.PSObject.Properties.Name -contains 'runtime' -and $manifest.runtime -eq 'win-arm64' -and
    [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() -ne 'Arm64') { throw 'This ARM64 bundle requires Windows on ARM. Download the x64 bundle for this PC.' }
if (-not [Environment]::Is64BitOperatingSystem) { throw 'This pre-built bundle requires 64-bit Windows.' }

if (-not $CodexPath) {
    $cachedBin = Join-Path $env:LOCALAPPDATA 'OpenAI\Codex\bin'
    if (Test-Path -LiteralPath $cachedBin) {
        $candidate = Get-ChildItem -LiteralPath $cachedBin -Directory | ForEach-Object {
            $path = Join-Path $_.FullName 'codex.exe'
            if (Test-Path -LiteralPath $path) { Get-Item -LiteralPath $path }
        } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if ($candidate) { $CodexPath = $candidate.FullName }
    }
    if (-not $CodexPath) {
        $command = Get-Command codex.exe -ErrorAction SilentlyContinue
        if ($command) { $CodexPath = $command.Source }
    }
}
if (-not $CodexPath -or -not (Test-Path -LiteralPath $CodexPath -PathType Leaf)) {
    throw 'The ChatGPT/Codex local CLI was not found. Open the installed desktop app first, or run Install.ps1 -CodexPath <full path to codex.exe>.'
}
& $CodexPath plugin add --help | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'This app CLI does not support local plugins. Update the desktop app before installing.' }

$userRoot = [Environment]::GetFolderPath('UserProfile')
$pluginsRoot = Join-Path $userRoot 'plugins'
$destination = Assert-Within (Join-Path $pluginsRoot $pluginName) $pluginsRoot
$marketplacePath = Join-Path $userRoot '.agents\plugins\marketplace.json'
if (Test-Path -LiteralPath $marketplacePath) {
    $marketplace = Get-Content -LiteralPath $marketplacePath -Raw | ConvertFrom-Json
    if ($marketplace.name -notmatch '^[A-Za-z0-9_-]+$') { throw 'Existing personal marketplace name is invalid.' }
    $existing = @($marketplace.plugins | Where-Object { $_.name -eq $pluginName })
    if ($existing.Count -gt 1) { throw 'Duplicate Better Computer Use marketplace entries require review.' }
    if ($existing.Count -eq 1 -and ($existing[0].source.source -ne 'local' -or $existing[0].source.path -ne "./plugins/$pluginName")) {
        throw 'Existing plugin points to a different source. It was not changed.'
    }
} else {
    $marketplace = [pscustomobject]@{ name = 'personal'; interface = [pscustomobject]@{ displayName = 'Personal' }; plugins = @() }
    $existing = @()
}

[IO.Directory]::CreateDirectory($pluginsRoot) | Out-Null
if (Test-Path -LiteralPath $destination) {
    $previousManifest = Join-Path $destination '.codex-plugin\plugin.json'
    if (-not (Test-Path -LiteralPath $previousManifest) -or (Get-Content -LiteralPath $previousManifest -Raw | ConvertFrom-Json).name -ne $pluginName) {
        throw 'Destination is not a recognized Better Computer Use plugin. Nothing was replaced.'
    }
    $backup = Assert-Within ($destination + '.backup-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff')) $pluginsRoot
    # Both recursive-move endpoints are resolved and checked inside the user's plugin directory.
    Move-Item -LiteralPath $destination -Destination $backup
    Write-Host "Previous plugin backed up to $backup"
}
[IO.Directory]::CreateDirectory($destination) | Out-Null
Get-ChildItem -LiteralPath (Join-Path $bundleRoot $pluginName) -Force | Copy-Item -Destination $destination -Recurse -Force
if ($existing.Count -eq 0) {
    if (Test-Path -LiteralPath $marketplacePath) { Copy-Item -LiteralPath $marketplacePath -Destination ($marketplacePath + '.backup-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff')) }
    $marketplace.plugins = @($marketplace.plugins) + @([pscustomobject]@{
        name = $pluginName
        source = [pscustomobject]@{ source = 'local'; path = "./plugins/$pluginName" }
        policy = [pscustomobject]@{ installation = 'AVAILABLE'; authentication = 'ON_INSTALL' }
        category = 'Productivity'
    })
    [IO.Directory]::CreateDirectory((Split-Path $marketplacePath)) | Out-Null
    [IO.File]::WriteAllText($marketplacePath, ($marketplace | ConvertTo-Json -Depth 100), $utf8)
}

& $CodexPath plugin add "$pluginName@$($marketplace.name)" --json
if ($LASTEXITCODE -ne 0) { throw 'Plugin files were installed, but app registration failed. The CLI diagnostic above explains why.' }
Write-Host ''
Write-Host 'Better Computer Use is installed. Start a NEW conversation in the desktop app.'
Write-Host 'Ask: Use Better Computer Use to start a separate desktop.'
Write-Host 'Checking local prerequisites:'
& (Join-Path $destination 'bin\BetterComputerUse.exe') doctor --auto-helper
if ($LASTEXITCODE -ne 0) { Write-Warning 'Installation succeeded, but Windows prerequisites need attention.' }
