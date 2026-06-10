@echo off
setlocal

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Installers\install_prerequisites.ps1"
if errorlevel 1 (
    echo.
    echo Plant Configurator prerequisites failed.
    pause
    exit /b %errorlevel%
)

endlocal
