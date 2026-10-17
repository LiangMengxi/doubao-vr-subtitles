@echo off
cd /d "%~dp0"
if exist "dist\DoubaoVR\DoubaoVR.exe" (
    start "" "dist\DoubaoVR\DoubaoVR.exe"
    exit /b
)
if not exist ".venv\Scripts\pythonw.exe" (
    echo Please run setup.cmd first.
    pause
    exit /b 1
)
start "" ".venv\Scripts\pythonw.exe" "app.py"
