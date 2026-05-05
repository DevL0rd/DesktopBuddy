@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
set "RESONITE_PATH=%SCRIPT_DIR%.."

net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator privileges...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -ArgumentList '%*' -Verb RunAs"
    exit /b
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%Setup-DesktopBuddy.ps1" -ResonitePath "%RESONITE_PATH%" %*
pause

endlocal
