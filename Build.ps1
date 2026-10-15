<#
.SYNOPSIS
    Build Plant Configuratora u ispravnom redoslijedu.

.DESCRIPTION
    Projekt CwcConfigurator.TiaHelper.V21 NIJE u .sln pa ga "dotnet restore" na
    solutionu preskoci. Bez njegovog restorea ne dobavi se implicitni paket
    Microsoft.NETFramework.ReferenceAssemblies i build pada s MSB3644.
    Restore i build zato MORAJU biti odvojeni pozivi -- restore unutar istog
    MSBuild poziva ne pomaze jer se nuget.g.props importa prerano.

    Detalji: 00_PREDAJA_PROJEKTA.md, tocka 3.

.EXAMPLE
    .\Build.ps1
    .\Build.ps1 -Test
    .\Build.ps1 -Publish
    .\Build.ps1 -Configuration Debug -Test
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$Test,

    [switch]$Publish,

    [string]$PublishDir = "$PSScriptRoot\publish_output",

    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$sln = Join-Path $PSScriptRoot 'PlantConfigurator_Source.sln'
$v21 = Join-Path $PSScriptRoot 'CwcConfigurator.TiaHelper.V21\CwcConfigurator.TiaHelper.V21.csproj'
$app = Join-Path $PSScriptRoot 'CwcConfigurator\CwcConfigurator.csproj'
$tests = Join-Path $PSScriptRoot 'CwcConfigurator.Tests\CwcConfigurator.Tests.csproj'

function Invoke-Step {
    param([string]$Label, [string[]]$DotnetArgs)

    Write-Host ''
    Write-Host "=== $Label ===" -ForegroundColor Cyan
    & dotnet @DotnetArgs
    if ($LASTEXITCODE -ne 0) {
        throw "$Label pao (exit $LASTEXITCODE)."
    }
}

if ($Clean) {
    Write-Host '=== Clean: brisem bin/obj ===' -ForegroundColor Cyan
    Get-ChildItem $PSScriptRoot -Recurse -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in 'bin', 'obj' } |
        Sort-Object { $_.FullName.Length } -Descending |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

# Korak 1: restore solutiona
Invoke-Step 'Restore (solution)' @('restore', $sln)

# Korak 2: restore V21 helpera -- nije u .sln, mora rucno (inace MSB3644)
Invoke-Step 'Restore (TiaHelper.V21 -- nije u .sln)' @('restore', $v21)

# Korak 3: build bez restorea
Invoke-Step "Build ($Configuration)" @('build', $sln, '-c', $Configuration, '--no-restore', '-v', 'minimal', '--nologo')

if ($Test) {
    Invoke-Step 'Testovi' @('test', $tests, '-c', $Configuration, '--no-build', '-v', 'minimal', '--nologo')
}

if ($Publish) {
    Invoke-Step "Publish -> $PublishDir" @('publish', $app, '-c', $Configuration, '-o', $PublishDir, '-v', 'minimal', '--nologo')
}

# Korak 4: TIA Trace Studio (Tools -> TIA Trace Studio...) u Tools\TiaTraceStudio pokraj PlantConfigurator.exe
$traceStudioPublish = ''
if ($Publish) { $traceStudioPublish = $PublishDir }
& (Join-Path $PSScriptRoot 'Build-TraceStudio.ps1') -Configuration $Configuration -PublishDir $traceStudioPublish

Write-Host ''
Write-Host 'Gotovo.' -ForegroundColor Green
Write-Host "  App:        CwcConfigurator\bin\$Configuration\net8.0-windows\PlantConfigurator.exe"
Write-Host "  Helperi:    CwcConfigurator\bin\$Configuration\net8.0-windows\TiaHelper\{V18,V21}\"
if ($Publish) { Write-Host "  Publish:    $PublishDir" }
