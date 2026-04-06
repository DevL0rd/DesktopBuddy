@echo off
SETLOCAL ENABLEDELAYEDEXPANSION

REM Get the script directory
set "SCRIPT_DIR=%~dp0"

REM Check for restart flag (-r, --restart) and desktop flag (-d)
REM Supports any combination: -r, -d, -r -d, -d -r, etc.
set RESTART=0
set DESKTOP=0

REM Check all arguments
for %%A in (%*) do (
    if /i "%%A"=="-r" set RESTART=1
    if /i "%%A"=="--restart" set RESTART=1
    if /i "%%A"=="-d" set DESKTOP=1
    if /i "%%A"=="--desktop" set DESKTOP=1
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
    if !DESKTOP! equ 1 (
        echo Starting Resonite in desktop mode...
        start steam://run/2519830//-Screen/
    ) else (
        start steam://rungameid/2519830
    )
)

ENDLOCAL
