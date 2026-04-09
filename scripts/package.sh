#!/bin/bash
SCRIPT_DIR="$(dirname "$0")"
ROOT_DIR="$SCRIPT_DIR/.."
BUILD_DIR="$ROOT_DIR/DesktopBuddy/bin/Debug/net10.0-windows10.0.22621.0"
OUT_ZIP="$ROOT_DIR/DesktopBuddy.zip"
STAGING=$(mktemp -d)

# Verify build exists
if [ ! -f "$BUILD_DIR/DesktopBuddy.dll" ]; then
    echo "ERROR: DesktopBuddy.dll not found. Run scripts/build.sh first."
    exit 1
fi

# Stage files
mkdir -p "$STAGING/rml_mods" "$STAGING/ffmpeg" "$STAGING/cloudflared" "$STAGING/softcam" "$STAGING/vbcable"

cp "$BUILD_DIR/DesktopBuddy.dll" "$STAGING/rml_mods/"
echo "  rml_mods/DesktopBuddy.dll"

for dll in "$ROOT_DIR"/ffmpeg/*.dll; do
    cp "$dll" "$STAGING/ffmpeg/"
    echo "  ffmpeg/$(basename "$dll")"
done

for dll in "$ROOT_DIR"/softcam/*.dll; do
    cp "$dll" "$STAGING/softcam/"
    echo "  softcam/$(basename "$dll")"
done

for f in "$ROOT_DIR"/vbcable/*; do
    cp "$f" "$STAGING/vbcable/"
    echo "  vbcable/$(basename "$f")"
done

cp "$ROOT_DIR/cloudflared/cloudflared.exe" "$STAGING/cloudflared/"
echo "  cloudflared/cloudflared.exe"

# Create zip
rm -f "$OUT_ZIP"
echo ""
echo "Creating zip..."

# Convert paths for PowerShell (needs Windows paths)
WIN_STAGING=$(cygpath -w "$STAGING" 2>/dev/null || echo "$STAGING")
WIN_ZIP=$(cygpath -w "$OUT_ZIP" 2>/dev/null || echo "$OUT_ZIP")
powershell.exe -Command "Compress-Archive -Path '${WIN_STAGING}\\*' -DestinationPath '${WIN_ZIP}'"

SIZE=$(du -m "$OUT_ZIP" | cut -f1)
echo ""
echo "Done: DesktopBuddy.zip (${SIZE} MB)"
echo "Extract into Resonite root folder."

rm -rf "$STAGING"
