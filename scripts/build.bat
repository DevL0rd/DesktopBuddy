@echo off
SETLOCAL ENABLEDELAYEDEXPANSION

REM Get the script directory
set "SCRIPT_DIR=%~dp0"

REM Check for restart flag
if /i "%1"=="--restart" (
    set RESTART=1
) else if /i "%1"=="-r" (
    set RESTART=1
) else (
    set RESTART=0
)

REM Kill processes if restart flag is set
if !RESTART! equ 1 (
    taskkill /F /IM Resonite.exe 2>nul
    taskkill /F /IM Renderite.Host.exe 2>nul
    taskkill /F /IM Renderite.Renderer.exe 2>nul
    taskkill /F /IM cloudflared.exe 2>nul
    timeout /t 2 /nobreak
)

REM Build the project
dotnet build "%SCRIPT_DIR%..\DesktopBuddy\DesktopBuddy.csproj"

REM Start Resonite if restart flag is set
if !RESTART! equ 1 (
    start steam://rungameid/2519830
)

ENDLOCAL
