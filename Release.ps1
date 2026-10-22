<#
.SYNOPSIS
    Izdaje novu verziju Plant Configuratora na GitHub Releases (Velopack).

.DESCRIPTION
    1. Build + testovi s brojem verzije (Build.ps1 -Test -Version).
    2. Skine prethodni release s GitHuba (za delta pakete).
    3. vpk pack: Setup.exe, full i delta paket u mapi Releases\.
    4. vpk upload github: objavi release s tagom v<verzija>.

    Instalirane aplikacije pri pokretanju (i svaka 4 sata) provjere GitHub Releases i ponude update.
    S -Mandatory je update obavezan: aplikacija se ne moze koristiti dok se ne instalira.

    Preduvjeti (jednom):
      dotnet tool install -g vpk --version 1.2.161
      gh auth login            (ili $env:GITHUB_TOKEN s pravom 'contents: write' na repo)
      CwcConfigurator\bugreport.token  (token za Help -> Report a bug; bez njega se prijava sprema kao .zip)

.EXAMPLE
    .\Release.ps1 -Version 1.1.0 -Notes "DB ALM filteri, HW AO push/pull"
    .\Release.ps1 -Version 1.1.1 -Mandatory -NotesFile notes.md
    .\Release.ps1 -Version 1.2.0 -NoUpload      # samo paket u Releases\, bez objave
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$Mandatory,

    [string]$Notes = '',

    [string]$NotesFile = '',

    [switch]$NoUpload,

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repoUrl = 'https://github.com/GepElectric/Plant-Configurator'
# Install folder is %LocalAppData%\<packId>; it must differ from %LocalAppData%\PlantConfigurator (logs, settings),
# because uninstall removes the whole install folder.
$packId = 'GepElectric.PlantConfigurator'
$appDir = Join-Path $PSScriptRoot 'CwcConfigurator\bin\Release\net8.0-windows'
$releasesDir = Join-Path $PSScriptRoot 'Releases'
$icon = Join-Path $PSScriptRoot 'CwcConfigurator\PlantConfigurator.ico'
# Not shipped: offline .NET installers (Setup.exe installs .NET 8 itself; .NET 4.8 is part of Windows 10/11),
# the old folder-start helpers and debug symbols.
# vpk matches the regex against the full file path.
$exclude = '\.pdb$|[\\/]Installers[\\/]|[\\/]Start Plant Configurator\.bat$'

function Invoke-Checked {
    param([string]$Label, [scriptblock]$Command)
    Write-Host ''
    Write-Host "=== $Label ===" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Label pao (exit $LASTEXITCODE)." }
}

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw "vpk nije instaliran: dotnet tool install -g vpk --version 1.2.161"
}

$token = $env:GITHUB_TOKEN
if (-not $token -and -not $NoUpload) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'Treba GitHub CLI (gh auth login) ili $env:GITHUB_TOKEN.' }
    $token = (& gh auth token 2>$null)
    if (-not $token) { throw 'Nisi prijavljen na GitHub: pokreni "gh auth login" (ili postavi $env:GITHUB_TOKEN).' }
}

# The release tag points at the built commit, so it has to be on GitHub already.
$commit = (& git -C $PSScriptRoot rev-parse HEAD).Trim()
if (-not $NoUpload) {
    if (& git -C $PSScriptRoot status --porcelain --untracked-files=no -- CwcConfigurator CwcConfigurator.TiaHelper CwcConfigurator.TiaHelper.V21 TiaTraceStudio) {
        throw 'Ima necommitanih izmjena u izvornom kodu: commitaj i pushaj prije releasea.'
    }
    & git -C $PSScriptRoot fetch --quiet origin
    if (-not (& git -C $PSScriptRoot branch -r --contains $commit)) {
        throw "Commit $commit nije na GitHubu: prvo git push."
    }
}

if (-not (Test-Path (Join-Path $PSScriptRoot 'CwcConfigurator\bugreport.token'))) {
    Write-Warning 'CwcConfigurator\bugreport.token ne postoji: Help -> Report a bug u ovom releaseu nece moci slati na GitHub (samo .zip).'
}

# Release notes; the marker line makes the update mandatory in the app (AppUpdateService.MandatoryMarker).
$notesText = $Notes
if ($NotesFile) { $notesText = (Get-Content $NotesFile -Raw -Encoding UTF8) }
if ($Mandatory) { $notesText = "[mandatory]`n`n$notesText" }
if (-not $notesText.Trim()) { $notesText = "Plant Configurator $Version" }
New-Item -ItemType Directory -Force $releasesDir | Out-Null
$notesPath = Join-Path $releasesDir "notes-$Version.md"
[IO.File]::WriteAllText($notesPath, $notesText, (New-Object Text.UTF8Encoding $false))

# 1) build
$buildArgs = @{ Version = $Version }
if (-not $SkipTests) { $buildArgs.Test = $true }
& (Join-Path $PSScriptRoot 'Build.ps1') @buildArgs

# 2) previous release (deltas); the very first release has none
if (-not $NoUpload) {
    Write-Host ''
    Write-Host '=== Prethodni release (za delta paket) ===' -ForegroundColor Cyan
    & vpk download github --repoUrl $repoUrl --token $token -o $releasesDir
    if ($LASTEXITCODE -ne 0) { Write-Warning 'Prethodni release nije skinut (prvi release?) - radi se samo full paket.' }
}

# 3) pack
Invoke-Checked "vpk pack $Version" {
    & vpk pack --packId $packId --packVersion $Version --packDir $appDir --mainExe 'PlantConfigurator.exe' --runtime 'win-x64' `
        --packTitle 'Plant Configurator' --packAuthors 'Geppert Electric' --icon $icon `
        --framework 'net8.0-x64-desktop' --exclude $exclude --releaseNotes $notesPath -o $releasesDir
}

if ($NoUpload) {
    Write-Host ''
    Write-Host "Paket je u $releasesDir (nije objavljen)." -ForegroundColor Green
    return
}

# 4) upload
Invoke-Checked "Objava v$Version na GitHub" {
    & vpk upload github --repoUrl $repoUrl --token $token -o $releasesDir --publish `
        --releaseName "Plant Configurator $Version" --tag "v$Version" --targetCommitish $commit
}

Write-Host ''
Write-Host "Objavljeno: $repoUrl/releases/tag/v$Version" -ForegroundColor Green
if ($Mandatory) { Write-Host 'Update je OBAVEZAN.' -ForegroundColor Yellow }
Write-Host "Prva instalacija na novom PC-u: $packId-win-Setup.exe s te stranice."
