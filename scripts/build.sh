#!/bin/bash
SCRIPT_DIR="$(dirname "$0")"
export MSYS_NO_PATHCONV=1

if [[ "$1" == "--restart" || "$1" == "-r" ]]; then
    taskkill.exe /F /IM Resonite.exe
    taskkill.exe /F /IM Renderite.Host.exe
    taskkill.exe /F /IM Renderite.Renderer.exe
    taskkill.exe /F /IM cloudflared.exe
    sleep 2
fi

dotnet build "$SCRIPT_DIR/../DesktopBuddy/DesktopBuddy.csproj"

if [[ "$1" == "--restart" || "$1" == "-r" ]]; then
    cmd.exe /c start steam://rungameid/2519830
fi
