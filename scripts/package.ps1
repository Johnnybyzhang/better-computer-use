param(
    [string]$Version,
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [ValidatePattern('^[A-Za-z0-9.-]+$')][string]$BuildId = [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$dotnetExe = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetExe)) { $dotnetExe = (Get-Command dotnet -ErrorAction Stop).Source }
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $projectRoot 'src/BetterComputerUse/BetterComputerUse.csproj') -Raw)).Project.PropertyGroup.Version }
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw 'Version must be a SemVer core with optional prerelease suffix.' }
$releaseId = $BuildId
$releaseName = "BetterComputerUse-$Version-$Runtime-$releaseId"
$stage = Join-Path $projectRoot "artifacts\$releaseName"
$plugin = Join-Path $stage 'better-computer-use'
$utf8 = New-Object System.Text.UTF8Encoding($false)
if (Test-Path -LiteralPath $stage) { throw 'Release staging directory already exists.' }
[IO.Directory]::CreateDirectory($plugin) | Out-Null
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'packaging\plugin') -Force | Copy-Item -Destination $plugin -Recurse
foreach ($file in @('Install.ps1', 'Install.cmd', 'START-HERE.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot "packaging\$file") -Destination $stage }
Push-Location $projectRoot
try {
    & $dotnetExe publish src\BetterComputerUse -c Release -r $Runtime --self-contained true -p:Version=$Version -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false --nologo -o (Join-Path $plugin 'bin')
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
} finally { Pop-Location }
# Publish does not copy NuGet runtime licenses. Include notices from the exact runtime packs used.
$assets = Get-Content -LiteralPath (Join-Path $projectRoot 'src\BetterComputerUse\obj\project.assets.json') -Raw | ConvertFrom-Json
$runtimeConfig = Get-Content -LiteralPath (Join-Path $plugin 'bin\BetterComputerUse.runtimeconfig.json') -Raw | ConvertFrom-Json
$packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
$noticeRoot = Join-Path $plugin 'third-party'
[IO.Directory]::CreateDirectory($noticeRoot) | Out-Null
foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
    $packageName = $framework.name.ToLowerInvariant() + '.runtime.' + $Runtime
    $packageDirectory = $null
    foreach ($root in $packageRoots) {
        $candidate = Join-Path $root ($packageName + '\' + $framework.version)
        if (Test-Path -LiteralPath $candidate) { $packageDirectory = $candidate; break }
    }
    if (-not $packageDirectory) { throw "Runtime license package not found: $packageName" }
    $noticeFiles = @(Get-ChildItem -LiteralPath $packageDirectory -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)' })
    if ($noticeFiles.Count -eq 0) { throw "No runtime license found in $packageDirectory" }
    $noticeDestination = Join-Path $noticeRoot $framework.name
    [IO.Directory]::CreateDirectory($noticeDestination) | Out-Null
    $noticeFiles | Copy-Item -Destination $noticeDestination
}
foreach ($notice in @('README.md','LICENSE','NOTICE.md','CONTRIBUTING.md','SECURITY.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot $notice) -Destination $plugin }
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $plugin -Recurse
$manifestPath = Join-Path $plugin '.codex-plugin\plugin.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$manifest.version = "$Version+codex.$releaseId"
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 20), $utf8)
$files = @(Get-ChildItem -LiteralPath $stage -Recurse -Force -File | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{ path = $_.FullName.Substring($stage.Length + 1).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
[IO.File]::WriteAllText((Join-Path $stage 'SHA256SUMS.json'), (@{ version = $manifest.version; runtime = $Runtime; revision = $env:GITHUB_SHA; files = $files } | ConvertTo-Json -Depth 5), $utf8)
# .NET ZIP APIs retain dot-directories; Compress-Archive can omit hidden plugin metadata.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = Join-Path $projectRoot "artifacts\$releaseName.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
[IO.File]::WriteAllText(($archive + '.sha256'), "$hash  $([IO.Path]::GetFileName($archive))`n", $utf8)
[pscustomobject]@{ Stage = $stage; Archive = $archive; SHA256 = $hash } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $projectRoot 'artifacts\latest-release.json')
Write-Host "Release ready: $archive"
