@echo off
SETLOCAL ENABLEDELAYEDEXPANSION

set "SCRIPT_DIR=%~dp0"
set "ROOT_DIR=%SCRIPT_DIR%.."
set "CONFIGURATION=Release"
set "TEXTURE_BRIDGE_DLL=%ROOT_DIR%\DesktopBuddySharedTextureBridge\bin\%CONFIGURATION%\net472\DesktopBuddySharedTextureBridge.dll"

for /f "delims=" %%D in ('dir /b /ad /o-n "%ROOT_DIR%\DesktopBuddy\bin\%CONFIGURATION%\net10.0-windows*" 2^>nul') do (
    if not defined MOD_DLL if exist "%ROOT_DIR%\DesktopBuddy\bin\%CONFIGURATION%\%%D\DesktopBuddy.dll" (
        set "MOD_OUT_DIR=%ROOT_DIR%\DesktopBuddy\bin\%CONFIGURATION%\%%D"
        set "MOD_DLL=!MOD_OUT_DIR!\DesktopBuddy.dll"
        set "MOD_SHA=!MOD_OUT_DIR!\DesktopBuddy.sha"
    )
)

for /f %%i in ('git -C "%ROOT_DIR%" rev-parse --short HEAD 2^>nul') do set "SHORT=%%i"
if not defined SHORT set "SHORT=unknown"
if not defined ZIP_NAME (
    for /f %%d in ('powershell -NoProfile -Command "Get-Date -Format yyyy.MM.dd_HH.mm.ss"') do set "DT=%%d"
    set "ZIP_NAME=DesktopBuddy-Alpha-!DT!_!SHORT!"
)

set "STAGE=%TEMP%\DesktopBuddyPackage\!ZIP_NAME!"
set "OUT_ZIP=%ROOT_DIR%\!ZIP_NAME!.zip"
set "INSTALL_SOURCE=%ROOT_DIR%\INSTALL.txt"
set "SETUP_BAT=%ROOT_DIR%\scripts\setup\Setup-DesktopBuddy.bat"
set "SETUP_PS1=%ROOT_DIR%\scripts\setup\Setup-DesktopBuddy.ps1"

if not defined MOD_DLL (
    echo ERROR: DesktopBuddy.dll not found under DesktopBuddy\bin\%CONFIGURATION%\net10.0-windows*. Run scripts\build.bat first.
    exit /b 1
)
if not exist "%MOD_DLL%" (
    echo ERROR: DesktopBuddy.dll not found under DesktopBuddy\bin\%CONFIGURATION%\net10.0-windows*. Run scripts\build.bat first.
    exit /b 1
)
if not exist "%TEXTURE_BRIDGE_DLL%" (
    echo ERROR: DesktopBuddySharedTextureBridge.dll not found. Run scripts\build.bat first.
    exit /b 1
)
if not exist "%INSTALL_SOURCE%" (
    echo ERROR: INSTALL.txt not found.
    exit /b 1
)
if not exist "%SETUP_BAT%" (
    echo ERROR: Setup-DesktopBuddy.bat not found.
    exit /b 1
)
if not exist "%SETUP_PS1%" (
    echo ERROR: Setup-DesktopBuddy.ps1 not found.
    exit /b 1
)

echo Building zip layout in: %STAGE%
echo Using DesktopBuddy build output: %MOD_OUT_DIR%
if exist "%STAGE%" rmdir /s /q "%STAGE%"
mkdir "%STAGE%"

REM rml_mods: mod DLL + sha
mkdir "%STAGE%\rml_mods"
copy "%MOD_DLL%" "%STAGE%\rml_mods\DesktopBuddy.dll" >nul
if exist "%MOD_SHA%" copy "%MOD_SHA%" "%STAGE%\rml_mods\DesktopBuddy.sha" >nul

REM Native dependencies: FFmpeg, SoftCam, cloudflared, and VB-Cable setup files.
REM Keep these outside rml_libs so Resonite Mod Loader does not try to load
REM native DLLs as managed assemblies.
mkdir "%STAGE%\DesktopBuddyNative"
copy "%ROOT_DIR%\DesktopBuddyNative\*" "%STAGE%\DesktopBuddyNative\" >nul

REM Renderer-side shared texture bridge
mkdir "%STAGE%\Renderer\BepInEx\plugins"
copy "%TEXTURE_BRIDGE_DLL%" "%STAGE%\Renderer\BepInEx\plugins\DesktopBuddySharedTextureBridge.dll" >nul

REM Setup scripts
mkdir "%STAGE%\setup"
copy "%SETUP_BAT%" "%STAGE%\setup\Setup-DesktopBuddy.bat" >nul
copy "%SETUP_PS1%" "%STAGE%\setup\Setup-DesktopBuddy.ps1" >nul

REM Install instructions included in the release zip
powershell -NoProfile -Command "(Get-Content -Raw '%INSTALL_SOURCE%').Replace('{{ZIP_NAME}}', '%ZIP_NAME%') | Set-Content -NoNewline '%STAGE%\INSTALL.txt'"
if errorlevel 1 ( echo ERROR: INSTALL.txt generation failed. & exit /b 1 )

REM Zip it
if exist "%OUT_ZIP%" del "%OUT_ZIP%"
powershell -NoProfile -Command "Compress-Archive -Path '%STAGE%\*' -DestinationPath '%OUT_ZIP%'"
if errorlevel 1 ( echo ERROR: Zip creation failed. & exit /b 1 )

REM Cleanup staging
rmdir /s /q "%STAGE%"

echo.
echo Done:
echo   !ZIP_NAME!.zip (extract to Resonite root)

ENDLOCAL

