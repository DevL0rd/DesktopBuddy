@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
set "RESONITE_PATH=%SCRIPT_DIR%.."
set "POWERSHELL_EXE="
@REM I swear this isn't slop it has a purpoise on different system and systems i have seen where powershell path is not set and weird stuff. please don't kill me lol <3
for %%D in ("%SystemRoot%\Sysnative\WindowsPowerShell" "%SystemRoot%\System32\WindowsPowerShell") do (
    if not defined POWERSHELL_EXE (
        if exist "%%~D" (
            for /f "delims=" %%F in ('where /r "%%~D" powershell.exe 2^>nul') do (
                if not defined POWERSHELL_EXE set "POWERSHELL_EXE=%%F"
            )
        )
    )
)

for %%P in (pwsh.exe powershell.exe) do (
    if not defined POWERSHELL_EXE (
        for /f "delims=" %%F in ('where %%P 2^>nul') do (
            if not defined POWERSHELL_EXE set "POWERSHELL_EXE=%%F"
        )
    )
)

if not defined POWERSHELL_EXE (
    echo ERROR: PowerShell was not found.
    echo DesktopBuddy setup requires Windows PowerShell or PowerShell 7.
    pause
    exit /b 1
)

net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator privileges...
    "%POWERSHELL_EXE%" -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -ArgumentList '%*' -Verb RunAs"
    exit /b
)

"%POWERSHELL_EXE%" -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%Setup-DesktopBuddy.ps1" -ResonitePath "%RESONITE_PATH%" %*
pause

endlocal
