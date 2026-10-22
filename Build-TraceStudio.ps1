<#
.SYNOPSIS
    Build TIA Trace Studio (Tools -> TIA Trace Studio...) u mapu Tools\TiaTraceStudio pokraj PlantConfigurator.exe.

.DESCRIPTION
    Izvorni kod je u TiaTraceStudio\ (WPF, .NET 10). Objavljuje se samostalno (self-contained, win-x64),
    pa na ciljnom racunalu ne treba .NET 10 runtime. Za build treba .NET 10 SDK: trazi se "dotnet" iz PATH-a
    ili lokalni SDK u %LOCALAPPDATA%\TiaTraceStudio\dotnet. Bez njega se Trace Studio preskace uz upozorenje
    (Plant Configurator se i dalje builda; Tools -> TIA Trace Studio tada pita gdje je TiaTraceStudio.exe).

    Poziva ga Build.ps1 nakon builda Plant Configuratora; moze se pokrenuti i sam.

.EXAMPLE
    .\Build-TraceStudio.ps1
    .\Build-TraceStudio.ps1 -Configuration Debug
    .\Build-TraceStudio.ps1 -PublishDir .\publish_output
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    # Publish mapa Plant Configuratora (Build.ps1 -Publish); Trace Studio ide i u njezin Tools\TiaTraceStudio.
    [string]$PublishDir = ''
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'TiaTraceStudio\src\TiaTraceStudio\TiaTraceStudio.csproj'
$staging = Join-Path $PSScriptRoot 'TiaTraceStudio\publish'
$appOutput = Join-Path $PSScriptRoot "CwcConfigurator\bin\$Configuration\net8.0-windows"

Write-Host ''
Write-Host '=== TIA Trace Studio -> Tools\TiaTraceStudio ===' -ForegroundColor Cyan

# .NET 10 SDK: iz PATH-a ako ga ima, inace lokalni SDK Trace Studija.
$sdk = $null
$pathDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($pathDotnet -and ((& $pathDotnet.Source --list-sdks) -match '^(1\d|[2-9]\d)\.')) { $sdk = $pathDotnet.Source }
if (-not $sdk) {
    $local = Join-Path $env:LOCALAPPDATA 'TiaTraceStudio\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $local) { $sdk = $local }
}
if (-not $sdk) {
    Write-Warning 'TIA Trace Studio preskocen: nije pronaden .NET 10 SDK (https://dotnet.microsoft.com/download/dotnet/10.0).'
    return
}

if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
& $sdk publish $project -c $Configuration -r win-x64 --self-contained true -o $staging -v minimal --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish TIA Trace Studija pao (exit $LASTEXITCODE)." }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'TiaTraceStudio\README.md') -Destination $staging -Force
$licenses = Join-Path $staging 'third-party'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'TiaTraceStudio\third-party') -File | Copy-Item -Destination $licenses -Force

function Install-To([string]$appFolder) {
    $target = Join-Path $appFolder 'Tools\TiaTraceStudio'
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -Path (Join-Path $staging '*') -Destination $target -Recurse -Force
    Write-Host "  TIA Trace Studio: $target\TiaTraceStudio.exe"
}

if (Test-Path -LiteralPath $appOutput) { Install-To $appOutput }
if ($PublishDir) { Install-To $PublishDir }
