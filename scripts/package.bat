@echo off
SETLOCAL ENABLEDELAYEDEXPANSION

set "SCRIPT_DIR=%~dp0"
set "ROOT_DIR=%SCRIPT_DIR%.."
set "BUILD_DIR=%ROOT_DIR%\DesktopBuddy\bin\Debug\net10.0-windows10.0.22621.0"
set "OUT_ZIP=%ROOT_DIR%\DesktopBuddy.zip"
set "STAGING=%TEMP%\DesktopBuddy_pkg_%RANDOM%"

REM Verify build exists
if not exist "%BUILD_DIR%\DesktopBuddy.dll" (
    echo ERROR: DesktopBuddy.dll not found. Run scripts\build.bat first.
    exit /b 1
)

REM Stage files
mkdir "%STAGING%\rml_mods" 2>nul
mkdir "%STAGING%\ffmpeg" 2>nul
mkdir "%STAGING%\cloudflared" 2>nul

copy "%BUILD_DIR%\DesktopBuddy.dll" "%STAGING%\rml_mods\" >nul
echo   rml_mods\DesktopBuddy.dll

for %%f in ("%ROOT_DIR%\ffmpeg\*.dll") do (
    copy "%%f" "%STAGING%\ffmpeg\" >nul
    echo   ffmpeg\%%~nxf
)

copy "%ROOT_DIR%\cloudflared\cloudflared.exe" "%STAGING%\cloudflared\" >nul
echo   cloudflared\cloudflared.exe

REM Create zip
if exist "%OUT_ZIP%" del "%OUT_ZIP%"
echo.
echo Creating zip...
powershell -Command "Compress-Archive -Path '%STAGING%\*' -DestinationPath '%OUT_ZIP%'"

echo.
echo Done: DesktopBuddy.zip
echo Extract into Resonite root folder.

rmdir /s /q "%STAGING%" 2>nul

ENDLOCAL
