param([switch]$All)
$ErrorActionPreference = 'Stop'
# Resolve installed runtime copies only; never copy or download proprietary OpenAI binaries.
$runtimeRoot = Join-Path $env:LOCALAPPDATA 'OpenAI\Codex\runtimes\cua_node'
$candidates = @()
if (Test-Path -LiteralPath $runtimeRoot) {
    $candidates += Get-ChildItem -LiteralPath $runtimeRoot -Directory | ForEach-Object {
        $candidate = Join-Path $_.FullName 'bin\node_modules\@oai\sky\bin\windows\codex-computer-use.exe'
        if (Test-Path -LiteralPath $candidate) { Get-Item -LiteralPath $candidate }
    }
}
$runtimeCandidates = @($candidates | Sort-Object LastWriteTimeUtc -Descending)
$packages = @(Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'OpenAI.*' })
foreach ($package in $packages) {
    $candidate = Join-Path $package.InstallLocation 'app\resources\cua_node\bin\node_modules\@oai\sky\bin\windows\codex-computer-use.exe'
    if (Test-Path -LiteralPath $candidate) { $candidates += Get-Item -LiteralPath $candidate }
}
$paths = @($candidates | Sort-Object LastWriteTimeUtc -Descending | Select-Object -ExpandProperty FullName -Unique)
if ($paths.Count -eq 0) { throw 'No installed Computer Use executable found. Supply its absolute path with --helper.' }
if ($All) { $paths }
elseif ($runtimeCandidates.Count -gt 0) { $runtimeCandidates[0].FullName }
else { throw 'No extracted cua_node runtime found. Initialize Computer Use in the installed app, or provide a verified helper path explicitly. MSIX package copies are listed with -All but are not selected automatically.' }
