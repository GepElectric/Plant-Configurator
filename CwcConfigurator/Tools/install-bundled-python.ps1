# One-time setup: downloads Python embeddable distribution into
# CwcConfigurator/Tools/python/ and pip-installs the packages needed by
# alm_par_doc.py. Run this script on the dev machine; commit the resulting
# Tools/python/ folder so end users get a self-contained Python runtime.

$ErrorActionPreference = 'Stop'

$pythonVersion = '3.12.4'
$pythonMajorMinor = 'python312'
$bundleDir = Join-Path $PSScriptRoot 'python'
$tempDir = Join-Path $env:TEMP ("plantcfg-python-" + [Guid]::NewGuid().ToString('N'))

Write-Host "Bundle target: $bundleDir"

if (Test-Path $bundleDir) {
    Write-Host "Removing existing $bundleDir ..."
    Remove-Item $bundleDir -Recurse -Force
}
New-Item -ItemType Directory -Path $bundleDir | Out-Null
New-Item -ItemType Directory -Path $tempDir | Out-Null

try {
    $embedZip = Join-Path $tempDir 'python-embed.zip'
    $embedUrl = "https://www.python.org/ftp/python/$pythonVersion/python-$pythonVersion-embed-amd64.zip"
    Write-Host "Downloading $embedUrl ..."
    Invoke-WebRequest -Uri $embedUrl -OutFile $embedZip -UseBasicParsing

    Write-Host "Extracting embeddable distribution ..."
    Expand-Archive -Path $embedZip -DestinationPath $bundleDir -Force

    # Embeddable distribution disables import site by default. Re-enable so pip
    # and pip-installed packages on Lib/site-packages are visible.
    $pthFile = Join-Path $bundleDir "$pythonMajorMinor._pth"
    if (-not (Test-Path $pthFile)) {
        throw "Missing $pthFile after extraction."
    }
    $pthLines = Get-Content $pthFile
    $pthLines = $pthLines | ForEach-Object {
        if ($_ -match '^#\s*import site') { 'import site' } else { $_ }
    }
    if (-not ($pthLines -contains 'Lib\site-packages')) {
        $pthLines += 'Lib\site-packages'
    }
    Set-Content -Path $pthFile -Value $pthLines -Encoding ASCII

    # Bootstrap pip via the official get-pip.py
    $getPip = Join-Path $tempDir 'get-pip.py'
    Write-Host "Downloading get-pip.py ..."
    Invoke-WebRequest -Uri 'https://bootstrap.pypa.io/get-pip.py' -OutFile $getPip -UseBasicParsing

    $pythonExe = Join-Path $bundleDir 'python.exe'
    Write-Host "Bootstrapping pip ..."
    & $pythonExe $getPip --no-warn-script-location
    if ($LASTEXITCODE -ne 0) { throw "pip bootstrap failed." }

    Write-Host "Installing python-docx ..."
    & $pythonExe -m pip install --no-warn-script-location python-docx
    if ($LASTEXITCODE -ne 0) { throw "python-docx install failed." }

    Write-Host "Installing pywin32 (optional, used for PDF export) ..."
    & $pythonExe -m pip install --no-warn-script-location pywin32
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "pywin32 install failed -- PDF export will require user-side setup or Microsoft Word."
    } else {
        # pywin32 needs a post-install step to register COM helpers. This is
        # best-effort under embeddable Python; failures are non-fatal.
        $postInstall = Join-Path $bundleDir 'Lib\site-packages\pywin32_postinstall.py'
        if (Test-Path $postInstall) {
            Write-Host "Running pywin32 post-install ..."
            try { & $pythonExe $postInstall -install -silent } catch { Write-Warning "pywin32 post-install warning: $_" }
        }
    }

    $exeSize = (Get-Item $pythonExe).Length
    $totalBytes = (Get-ChildItem $bundleDir -Recurse | Measure-Object -Property Length -Sum).Sum
    Write-Host ("Bundled Python: {0}" -f $pythonExe)
    Write-Host ("python.exe size: {0:N0} bytes" -f $exeSize)
    Write-Host ("Total bundle size: {0:N1} MB" -f ($totalBytes / 1MB))
    Write-Host "Done. Commit the Tools/python/ folder to the repository."
}
finally {
    if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue }
}
