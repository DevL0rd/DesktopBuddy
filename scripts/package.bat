@echo off
SETLOCAL ENABLEDELAYEDEXPANSION

set "SCRIPT_DIR=%~dp0"
set "ROOT_DIR=%SCRIPT_DIR%.."
set "BUILD_DIR=%ROOT_DIR%\DesktopBuddy\bin\Debug\net10.0-windows10.0.22621.0"
set "INSTALLER_DIR=%ROOT_DIR%\DesktopBuddyManager\bin\Debug\net10.0-windows10.0.22621.0"
set "MANAGER_DIR=%ROOT_DIR%\DesktopBuddyManager\bin\Debug\net10.0-windows10.0.22621.0"

for /f %%i in ('git -C "%ROOT_DIR%" rev-parse --short HEAD 2^>nul') do set "SHORT=%%i"
if not defined SHORT set "SHORT=unknown"
if not defined ZIP_NAME (
    for /f %%d in ('powershell -NoProfile -Command "Get-Date -Format yyyy.MM.dd_HH.mm.ss"') do set "DT=%%d"
    set "ZIP_NAME=DesktopBuddy-Alpha-Manager_!DT!_!SHORT!"
)

set "OUT_EXE=%ROOT_DIR%\!ZIP_NAME!.exe"

if not exist "%BUILD_DIR%\DesktopBuddy.dll" (
    echo ERROR: DesktopBuddy.dll not found. Run scripts\build.bat first.
    exit /b 1
)

echo Building manager...
dotnet publish "%ROOT_DIR%\DesktopBuddyManager\DesktopBuddyManager.csproj" -r win-x64 --self-contained false -p:PublishSingleFile=true -o "%ROOT_DIR%\DesktopBuddyManager\publish" /nologo /verbosity:quiet
if errorlevel 1 ( echo ERROR: Manager build failed. & exit /b 1 )

if exist "!OUT_EXE!" del "!OUT_EXE!"
copy "%ROOT_DIR%\DesktopBuddyManager\publish\Manager.exe" "!OUT_EXE!" >nul

echo.
echo Done: !ZIP_NAME!.exe
echo Run as administrator to manage DesktopBuddy.

ENDLOCAL
