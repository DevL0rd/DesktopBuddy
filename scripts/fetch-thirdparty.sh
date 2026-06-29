#!/usr/bin/env bash
set -euo pipefail

GYAN_VERSION="8.1.2"
GYAN_ZIP="https://github.com/GyanD/codexffmpeg/releases/download/${GYAN_VERSION}/ffmpeg-${GYAN_VERSION}-full_build-shared.zip"
GYAN_PAGE="https://github.com/GyanD/codexffmpeg/releases/tag/${GYAN_VERSION}"

CF_VERSION="2026.6.1"
CF_WIN="https://github.com/cloudflare/cloudflared/releases/download/${CF_VERSION}/cloudflared-windows-amd64.exe"
CF_PAGE="https://github.com/cloudflare/cloudflared/releases/tag/${CF_VERSION}"

VBCABLE_ZIP="https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip"
VBCABLE_PAGE="https://vb-audio.com/Cable/"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
runtime="$root/DesktopBuddyRuntime"
work="$root/obj/thirdparty"
native="$root/DesktopBuddyLinuxBridge/bin/Release"

mode="download"
force=0
case "${1:-}" in
  --manifest) mode="manifest" ;;
  --force) force=1 ;;
esac
[[ "${THIRDPARTY_FORCE:-0}" == "1" ]] && force=1

need() { command -v "$1" >/dev/null 2>&1 || { echo "Required tool '$1' not found on PATH." >&2; exit 127; }; }

dl() {
  echo "Downloading $(basename "$2")"
  curl -fsSL --retry 3 -o "$2" "$1"
}

fetch_all() {
  for t in curl unzip; do need "$t"; done
  mkdir -p "$runtime" "$work"

  if [[ "$force" -eq 1 || ! -f "$runtime/avcodec-62.dll" ]]; then
    dl "$GYAN_ZIP" "$work/gyan.zip"
    unzip -o -j "$work/gyan.zip" \
      "*/bin/avcodec-62.dll" "*/bin/avformat-62.dll" \
      "*/bin/avutil-60.dll" "*/bin/swresample-6.dll" -d "$runtime" >/dev/null
  fi

  if [[ "$force" -eq 1 || ! -f "$runtime/cloudflared.exe" ]]; then
    dl "$CF_WIN" "$runtime/cloudflared.exe"
  fi

  if [[ "$force" -eq 1 || ! -f "$runtime/VBCABLE_Setup_x64.exe" ]]; then
    dl "$VBCABLE_ZIP" "$work/vbcable.zip"
    unzip -o -j "$work/vbcable.zip" "VBCABLE_Setup_x64.exe" -d "$runtime" >/dev/null
  fi

  echo "Third-party runtime binaries ready in $runtime"
}

hash_of() { sha256sum "$1" 2>/dev/null | cut -d' ' -f1; }

row() {
  local p="$runtime/$1"
  if [[ -f "$p" ]]; then
    echo "| \`$1\` | [download]($2) | [source]($3) | \`$(hash_of "$p")\` |"
  else
    echo "| \`$1\` | [download]($2) | [source]($3) | (not present) |"
  fi
}

manifest() {
  need sha256sum
  echo "## Third-party binary manifest"
  echo
  echo "Downloaded from official sources at build time; none are committed to the repo. Verify each \`sha256\` below against the file at its **source** link."
  echo
  echo "| File | Download | Source / published hashes | sha256 |"
  echo "| --- | --- | --- | --- |"
  row "avcodec-62.dll"        "$GYAN_ZIP" "$GYAN_PAGE"
  row "avformat-62.dll"       "$GYAN_ZIP" "$GYAN_PAGE"
  row "avutil-60.dll"         "$GYAN_ZIP" "$GYAN_PAGE"
  row "swresample-6.dll"      "$GYAN_ZIP" "$GYAN_PAGE"
  row "cloudflared.exe"       "$CF_WIN"   "$CF_PAGE"
  row "VBCABLE_Setup_x64.exe" "$VBCABLE_ZIP" "$VBCABLE_PAGE"
  echo
  echo "## Built from source this run"
  echo
  echo "| File | Source | sha256 |"
  echo "| --- | --- | --- |"
  for so in libdesktopbuddy_linux_native.so DesktopBuddyLinuxBridge.so libdesktopbuddy_linux_stream.so; do
    [[ -f "$native/$so" ]] && echo "| \`$so\` | DesktopBuddy source in this repo | \`$(hash_of "$native/$so")\` |"
  done
  echo
  echo "## Built from source this run (fork, windows-latest MSBuild)"
  echo
  echo "| File | Source | sha256 |"
  echo "| --- | --- | --- |"
  for s in softcam.dll softcam64.dll; do
    [[ -f "$runtime/$s" ]] && echo "| \`$s\` | [DevL0rd/softcam fork of tshino/softcam](https://github.com/DevL0rd/softcam) | \`$(hash_of "$runtime/$s")\` |"
  done
}

if [[ "$mode" == "manifest" ]]; then
  manifest
else
  fetch_all
fi
