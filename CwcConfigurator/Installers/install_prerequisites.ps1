param(
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

function Test-WindowsDesktopRuntime8 {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) {
        return $false
    }

    $runtimes = & $dotnet.Source --list-runtimes 2>$null
    return [bool]($runtimes | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App\s+8\.' })
}

function Test-NetFramework48 {
    $release = Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -Name Release -ErrorAction SilentlyContinue
    return ($release -as [int]) -ge 528040
}

$installersDir = $PSScriptRoot
$appRoot = Split-Path -Parent $installersDir

if (-not (Test-WindowsDesktopRuntime8)) {
    $desktopRuntime = Get-ChildItem -LiteralPath $installersDir -Filter 'windowsdesktop-runtime-8*-win-x64.exe' -File -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        Select-Object -First 1

    if ($null -eq $desktopRuntime) {
        [System.Windows.Forms.MessageBox]::Show(
            'Microsoft Windows Desktop Runtime 8 is missing and the installer was not found in the Installers folder.',
            'Plant Configurator prerequisites',
            'OK',
            'Error') | Out-Null
        exit 1
    }

    $process = Start-Process -FilePath $desktopRuntime.FullName -ArgumentList '/install /passive /norestart' -Wait -PassThru
    if ($process.ExitCode -notin 0, 3010, 1641) {
        [System.Windows.Forms.MessageBox]::Show(
            "Microsoft Windows Desktop Runtime 8 installer failed. Exit code: $($process.ExitCode)",
            'Plant Configurator prerequisites',
            'OK',
            'Error') | Out-Null
        exit $process.ExitCode
    }
}

if (-not (Test-NetFramework48)) {
    $frameworkInstaller = Get-ChildItem -LiteralPath $installersDir -Filter 'ndp48*.exe' -File -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        Select-Object -First 1

    if ($null -ne $frameworkInstaller) {
        $process = Start-Process -FilePath $frameworkInstaller.FullName -ArgumentList '/passive /norestart' -Wait -PassThru
        if ($process.ExitCode -notin 0, 3010, 1641) {
            [System.Windows.Forms.MessageBox]::Show(
                ".NET Framework 4.8 installer failed. Exit code: $($process.ExitCode)",
                'Plant Configurator prerequisites',
                'OK',
                'Error') | Out-Null
            exit $process.ExitCode
        }
    }
}

if (-not $NoLaunch) {
    $appPath = Join-Path $appRoot 'PlantConfigurator.exe'
    if (Test-Path -LiteralPath $appPath) {
        Start-Process -FilePath $appPath -WorkingDirectory $appRoot
    }
}
