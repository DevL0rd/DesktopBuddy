#!/bin/bash
SCRIPT_DIR="$(dirname "$0")"
export MSYS_NO_PATHCONV=1

# Check for restart flag (-r, --restart) and desktop flag (-d)
# Supports any combination: -r, -d, -r -d, -d -r, etc.
RESTART=0
DESKTOP=0

# Check all arguments
for arg in "$@"; do
    if [[ "$arg" == "-r" || "$arg" == "--restart" ]]; then
        RESTART=1
    fi
    if [[ "$arg" == "-d" || "$arg" == "--desktop" ]]; then
        DESKTOP=1
    fi
done

if [[ $RESTART -eq 1 ]]; then
    taskkill.exe /F /IM Resonite.exe 2>/dev/null
    taskkill.exe /F /IM Renderite.Host.exe 2>/dev/null
    taskkill.exe /F /IM Renderite.Renderer.exe 2>/dev/null
    taskkill.exe /F /IM cloudflared.exe 2>/dev/null
    sleep 2
fi

dotnet build "$SCRIPT_DIR/../DesktopBuddy/DesktopBuddy.csproj"

if [[ $RESTART -eq 1 ]]; then
    if [[ $DESKTOP -eq 1 ]]; then
        cmd.exe /c start steam://run/2519830//-Screen/
    else
        cmd.exe /c start steam://rungameid/2519830
    fi
fi
