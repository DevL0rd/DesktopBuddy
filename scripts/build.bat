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

REM Build the mod (game-side)
dotnet build "%SCRIPT_DIR%..\DesktopBuddy\DesktopBuddy.csproj"
if !ERRORLEVEL! neq 0 (
    echo MOD BUILD FAILED — not launching Resonite
    exit /b !ERRORLEVEL!
)

REM Build the native renderer helper
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "MSBUILD_EXE="
if exist "!VSWHERE!" (
    for /f "usebackq tokens=*" %%I in (`"!VSWHERE!" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find MSBuild\**\Bin\MSBuild.exe`) do (
        if not defined MSBUILD_EXE set "MSBUILD_EXE=%%I"
    )
)
if not defined MSBUILD_EXE (
    echo NATIVE BUILD FAILED - Visual Studio Build Tools with C++ x64 tools were not found
    exit /b 1
)
"!MSBUILD_EXE!" "%SCRIPT_DIR%..\DesktopBuddyRendererNative\DesktopBuddyRendererNative.vcxproj" /p:Configuration=Release /p:Platform=x64 /m
if !ERRORLEVEL! neq 0 (
    echo NATIVE BUILD FAILED - not launching Resonite
    exit /b !ERRORLEVEL!
)

REM Build the renderer plugin
dotnet build "%SCRIPT_DIR%..\DesktopBuddyRenderer\DesktopBuddyRenderer.csproj"
if !ERRORLEVEL! neq 0 (
    echo RENDERER BUILD FAILED — not launching Resonite
    exit /b !ERRORLEVEL!
)

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
