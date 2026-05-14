@echo off
SETLOCAL ENABLEDELAYEDEXPANSION

REM Get the script directory
set "SCRIPT_DIR=%~dp0"

REM Check for restart flag (-r, --restart) and desktop flag (-d)
REM Supports any combination: -r, -d, -r -d, -d -r, etc.
set RESTART=0
set DESKTOP=0
set CONFIGURATION=Release
set "DOTNET_BUILD_ARGS="

REM GitHub-hosted builds should use the checked-in reference DLLs and pinned
REM NuGet packages, not any Resonite install that might exist on the runner.
if /i "%GITHUB_ACTIONS%"=="true" set "DOTNET_BUILD_ARGS=/p:ResonitePath=C:\__DesktopBuddyNoDeploy__"

REM Check all arguments
for %%A in (%*) do (
    if /i "%%A"=="-r" set RESTART=1
    if /i "%%A"=="--restart" set RESTART=1
    if /i "%%A"=="-d" set DESKTOP=1
    if /i "%%A"=="--desktop" set DESKTOP=1
)

REM Kill processes if restart flag is set
if !RESTART! equ 1 (
    call :KillProcessTree Resonite.exe
    call :KillProcessTree Renderite.Host.exe
    call :KillProcessTree Renderite.Renderer.exe
    call :KillDesktopBuddyCloudflared
    call :WaitForExit Resonite.exe Renderite.Host.exe Renderite.Renderer.exe
    call :KillDesktopBuddyCloudflared
    call :WaitForDesktopBuddyCloudflared
    if !ERRORLEVEL! neq 0 (
        echo FAILED TO STOP RESONITE - not building or restarting
        exit /b !ERRORLEVEL!
    )
    ping -n 3 127.0.0.1 >nul
)

REM Build the mod (game-side)
dotnet build "%SCRIPT_DIR%..\DesktopBuddy\DesktopBuddy.csproj" -c %CONFIGURATION% %DOTNET_BUILD_ARGS%
if !ERRORLEVEL! neq 0 (
    echo MOD BUILD FAILED - not launching Resonite
    exit /b !ERRORLEVEL!
)

REM Build the renderer-side shared texture bridge
dotnet build "%SCRIPT_DIR%..\DesktopBuddySharedTextureBridge\DesktopBuddySharedTextureBridge.csproj" -c %CONFIGURATION% %DOTNET_BUILD_ARGS%
if !ERRORLEVEL! neq 0 (
    echo SHARED TEXTURE BRIDGE BUILD FAILED - not launching Resonite
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
exit /b 0

:KillDesktopBuddyCloudflared
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$procs = Get-CimInstance Win32_Process -Filter \"name='cloudflared.exe'\" | Where-Object { ($_.ExecutablePath -like '*\DesktopBuddyNative\cloudflared.exe') -or ($_.CommandLine -like '*--url http://localhost:48080*') }; " ^
  "foreach ($p in $procs) { Write-Host ('Stopping DesktopBuddy cloudflared.exe PID ' + $p.ProcessId); Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }"
exit /b 0

:WaitForDesktopBuddyCloudflared
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$filter = { ($_.ExecutablePath -like '*\DesktopBuddyNative\cloudflared.exe') -or ($_.CommandLine -like '*--url http://localhost:48080*') }; " ^
  "$deadline = (Get-Date).AddSeconds(20); " ^
  "do { $procs = @(Get-CimInstance Win32_Process -Filter \"name='cloudflared.exe'\" | Where-Object $filter); if ($procs.Count -eq 0) { exit 0 }; foreach ($p in $procs) { Write-Host ('Waiting: stopping DesktopBuddy cloudflared.exe PID ' + $p.ProcessId); Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }; Start-Sleep -Milliseconds 500 } while ((Get-Date) -lt $deadline); " ^
  "$procs = @(Get-CimInstance Win32_Process -Filter \"name='cloudflared.exe'\" | Where-Object $filter); if ($procs.Count -eq 0) { exit 0 }; Write-Host 'WARNING: DesktopBuddy cloudflared.exe is still running after forced shutdown:'; $procs | Select-Object Name,ProcessId,ParentProcessId,ExecutablePath,CommandLine | Format-List; exit 1"
if !ERRORLEVEL! neq 0 exit /b !ERRORLEVEL!
exit /b 0

:KillProcessTree
set "PROC_NAME=%~1"
tasklist /FI "IMAGENAME eq %PROC_NAME%" /NH 2>nul | find /I "%PROC_NAME%" >nul
if !ERRORLEVEL! equ 0 (
    echo Stopping %PROC_NAME%...
    taskkill /F /T /IM "%PROC_NAME%" 2>nul
)
exit /b 0

:WaitForExit
set "WAIT_ATTEMPT=0"
:WaitForExitLoop
set "ANY_RUNNING=0"
for %%P in (%*) do (
    tasklist /FI "IMAGENAME eq %%P" /NH 2>nul | find /I "%%P" >nul
    if !ERRORLEVEL! equ 0 set "ANY_RUNNING=1"
)
if "!ANY_RUNNING!"=="0" exit /b 0

set /a WAIT_ATTEMPT+=1
if !WAIT_ATTEMPT! geq 20 (
    echo WARNING: Some Resonite processes are still running after forced shutdown:
    for %%P in (%*) do (
        tasklist /FI "IMAGENAME eq %%P" /NH 2>nul | find /I "%%P"
    )
    exit /b 1
)

for %%P in (%*) do taskkill /F /T /IM "%%P" 2>nul
ping -n 2 127.0.0.1 >nul
goto :WaitForExitLoop
