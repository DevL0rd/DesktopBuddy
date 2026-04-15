@echo off
SETLOCAL ENABLEDELAYEDEXPANSION

set "SCRIPT_DIR=%~dp0"
set "ROOT_DIR=%SCRIPT_DIR%.."
set "MOD_DLL=%ROOT_DIR%\DesktopBuddy\bin\Debug\net10.0-windows10.0.22621.0\DesktopBuddy.dll"
set "MOD_SHA=%ROOT_DIR%\DesktopBuddy\bin\Debug\net10.0-windows10.0.22621.0\DesktopBuddy.sha"
set "RENDERER_DLL=%ROOT_DIR%\DesktopBuddyRenderer\bin\Debug\net472\DesktopBuddyRenderer.dll"

for /f %%i in ('git -C "%ROOT_DIR%" rev-parse --short HEAD 2^>nul') do set "SHORT=%%i"
if not defined SHORT set "SHORT=unknown"
if not defined ZIP_NAME (
    for /f %%d in ('powershell -NoProfile -Command "Get-Date -Format yyyy.MM.dd_HH.mm.ss"') do set "DT=%%d"
    set "ZIP_NAME=DesktopBuddy-Alpha-!DT!_!SHORT!"
)

set "STAGE=%TEMP%\DesktopBuddyPackage\!ZIP_NAME!"
set "OUT_ZIP=%ROOT_DIR%\!ZIP_NAME!.zip"

if not exist "%MOD_DLL%" (
    echo ERROR: DesktopBuddy.dll not found. Run scripts\build.bat first.
    exit /b 1
)
if not exist "%RENDERER_DLL%" (
    echo ERROR: DesktopBuddyRenderer.dll not found. Run scripts\build.bat first.
    exit /b 1
)

echo Packaging manager...
dotnet publish "%ROOT_DIR%\DesktopBuddyManager\DesktopBuddyManager.csproj" -r win-x64 --self-contained false -p:PublishSingleFile=true -o "%ROOT_DIR%\DesktopBuddyManager\publish" /nologo /verbosity:quiet
if errorlevel 1 ( echo ERROR: Manager publish failed. & exit /b 1 )

echo Building zip layout in: %STAGE%
if exist "%STAGE%" rmdir /s /q "%STAGE%"
mkdir "%STAGE%"

REM DesktopBuddyManager.exe at root
copy "%ROOT_DIR%\DesktopBuddyManager\publish\DesktopBuddyManager.exe" "%STAGE%\DesktopBuddyManager.exe" >nul

REM rml_mods: mod DLL + sha
mkdir "%STAGE%\rml_mods"
copy "%MOD_DLL%" "%STAGE%\rml_mods\DesktopBuddy.dll" >nul
if exist "%MOD_SHA%" copy "%MOD_SHA%" "%STAGE%\rml_mods\DesktopBuddy.sha" >nul

REM rml_libs: ffmpeg, softcam, cloudflared
mkdir "%STAGE%\rml_libs"
copy "%ROOT_DIR%\rml_libs\*" "%STAGE%\rml_libs\" >nul

REM Renderer BepInEx plugin
mkdir "%STAGE%\Renderer\BepInEx\plugins"
copy "%RENDERER_DLL%" "%STAGE%\Renderer\BepInEx\plugins\DesktopBuddyRenderer.dll" >nul

REM VBCable installer (keeps its own subfolder so .inf/.sys are next to the exe)
mkdir "%STAGE%\vbcable"
xcopy /e /q "%ROOT_DIR%\vbcable\*" "%STAGE%\vbcable\" >nul

REM Zip it
if exist "%OUT_ZIP%" del "%OUT_ZIP%"
powershell -NoProfile -Command "Compress-Archive -Path '%STAGE%\*' -DestinationPath '%OUT_ZIP%'"
if errorlevel 1 ( echo ERROR: Zip creation failed. & exit /b 1 )

REM Cleanup staging
rmdir /s /q "%STAGE%"

REM Also publish standalone DesktopBuddyManager.exe for direct download
set "OUT_EXE=%ROOT_DIR%\!ZIP_NAME!-DesktopBuddyManager.exe"
if exist "!OUT_EXE!" del "!OUT_EXE!"
copy "%ROOT_DIR%\DesktopBuddyManager\publish\DesktopBuddyManager.exe" "!OUT_EXE!" >nul

echo.
echo Done:
echo   !ZIP_NAME!.zip                     (extract to Resonite root)
echo   !ZIP_NAME!-DesktopBuddyManager.exe (standalone - downloads zip on first run)
echo Run DesktopBuddyManager.exe as administrator.

ENDLOCAL

