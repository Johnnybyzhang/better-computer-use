param([switch]$Test, [switch]$Ci)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$localSdk = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnetExe = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $projectRoot
try {
    & $dotnetExe build src\BetterComputerUse -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if ($Test) {
        if ($Ci) { & $dotnetExe run --project tests\BetterComputerUse.Tests -c Release --no-launch-profile -- --ci }
        else { & $dotnetExe run --project tests\BetterComputerUse.Tests -c Release --no-launch-profile }
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    & $dotnetExe publish src\BetterComputerUse -c Release --no-restore --nologo -o artifacts\app
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
} finally { Pop-Location }
