param([string]$TestCsv, [string]$OutputDirectory = 'artifacts/release')
$ErrorActionPreference = 'Stop'
$studioSdk = Join-Path $env:LOCALAPPDATA 'TiaTraceStudio/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $studioSdk)) { $studioSdk = (Get-Command dotnet -ErrorAction Stop).Source }
Push-Location $PSScriptRoot
try {
    & $studioSdk build src/TiaTraceStudio -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if ($TestCsv) {
        & $studioSdk run --project tests/TiaTraceStudio.Checks -c Release -- $TestCsv artifacts/checks
        if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
    }
    & $studioSdk publish src/TiaTraceStudio -c Release -r win-x64 --self-contained true -o $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath README.md -Destination (Join-Path $OutputDirectory 'README.md') -Force
    $licenseOutput = Join-Path $OutputDirectory 'third-party'
    New-Item -ItemType Directory -Path $licenseOutput -Force | Out-Null
    Get-ChildItem -LiteralPath third-party -File | Copy-Item -Destination $licenseOutput -Force
    Compress-Archive -Path (Join-Path $OutputDirectory '*') -DestinationPath artifacts/TiaTraceStudio-win-x64.zip -Force
    Write-Host "Ready: $OutputDirectory/TiaTraceStudio.exe"
} finally { Pop-Location }
