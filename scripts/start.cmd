@echo off
setlocal
set "APP_DIR=%~dp0..\native-rewrite\ui\bin\Release\net8.0-windows10.0.19041.0\win-x64"
if not exist "%APP_DIR%\DoubaoVR.exe" set "APP_DIR=%~dp0..\native-rewrite\out\app"
if not exist "%APP_DIR%\DoubaoVR.exe" (
    echo Build the current application first:
    echo pwsh -NoProfile -File "%~dp0build.ps1" -PrepareToolchain
    pause
    exit /b 1
)
start "" /D "%APP_DIR%" "%APP_DIR%\DoubaoVR.exe"
