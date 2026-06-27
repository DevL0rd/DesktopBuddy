#!/usr/bin/env bash
set -euo pipefail

# Linux/CI packager. Produces the same Thunderstore/manual zips as scripts/package.ps1,
# and additionally bundles the Linux native libraries built by scripts/build.sh.
# No PowerShell required.

configuration="Release"
package="Manual"

usage() {
  cat <<'EOF'
Usage: scripts/package.sh [options]

Options:
  -c, --configuration NAME   Build configuration. Default: Release.
  -p, --package KIND         Manual | Main | Runtime | All. Default: Manual.
  -h, --help                 Show this help.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    -c|--configuration) configuration="${2:?missing configuration}"; shift 2 ;;
    -p|--package) package="${2:?missing package}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "$root"

for tool in jq md5sum python3; do
  command -v "$tool" >/dev/null 2>&1 || { echo "Required tool '$tool' not found on PATH." >&2; exit 127; }
done

version="$(tr -d '[:space:]' < "$root/VERSION")"
runtime_version="$(tr -d '[:space:]' < "$root/RUNTIME_VERSION")"
toml="$root/thunderstore.toml"

semver_re='^[0-9]+\.[0-9]+\.[0-9]+$'
[[ "$version" =~ $semver_re ]] || { echo "VERSION must be Major.Minor.Patch: '$version'" >&2; exit 1; }
[[ "$runtime_version" =~ $semver_re ]] || { echo "RUNTIME_VERSION must be Major.Minor.Patch: '$runtime_version'" >&2; exit 1; }

toml_string() {
  local key="$1"
  sed -nE "s/^[[:space:]]*${key}[[:space:]]*=[[:space:]]*\"(.*)\"[[:space:]]*$/\1/p" "$toml" | head -n1
}

namespace="$(toml_string namespace)"
main_name="$(toml_string name)"
description="$(toml_string description)"
website_url="$(toml_string websiteUrl)"
toml_version="$(toml_string versionNumber)"
runtime_dir_name="DesktopBuddyRuntime"
runtime_package_name="DesktopBuddyRuntime"

[[ -n "$namespace" && -n "$main_name" ]] || { echo "namespace/name not found in thunderstore.toml" >&2; exit 1; }
if [[ "$toml_version" != "$version" ]]; then
  echo "VERSION ($version) does not match thunderstore.toml versionNumber ($toml_version). Run scripts/sync-version.ps1 or sync manually." >&2
  exit 1
fi

# Dependencies from [package.dependencies] as "Name-Version", excluding the runtime package
# (re-added explicitly with RUNTIME_VERSION), to match package.ps1.
mapfile -t base_dependencies < <(
  awk '/^\[package\.dependencies\]/{f=1;next} /^\[/{f=0} f' "$toml" |
  sed -nE 's/^[[:space:]]*([A-Za-z0-9_.-]+)[[:space:]]*=[[:space:]]*"([^"]+)".*/\1-\2/p' |
  grep -v '^DevL0rd-DesktopBuddyRuntime-' || true
)
main_dependencies=("${base_dependencies[@]}" "DevL0rd-DesktopBuddyRuntime-${runtime_version}")

mod_out=""
find_mod_out() {
  local base="$root/DesktopBuddy/bin/$configuration"
  mod_out="$(find "$base" -maxdepth 1 -type d -name 'net10.0-windows*' 2>/dev/null | sort -r | while read -r d; do
    [[ -f "$d/DesktopBuddy.dll" ]] && { printf '%s\n' "$d"; break; }
  done)"
  [[ -n "$mod_out" ]] || { echo "DesktopBuddy.dll not found under DesktopBuddy/bin/$configuration/net10.0-windows*. Run scripts/build.sh first." >&2; exit 1; }
}
find_mod_out

bridge_dll="$root/DesktopBuddySharedTextureBridge/bin/$configuration/net472/DesktopBuddySharedTextureBridge.dll"
runtime_source="$root/$runtime_dir_name"
native_dir="$root/DesktopBuddyLinuxBridge/bin/$configuration"
managed_deps=("FFmpeg.AutoGen.dll" "Microsoft.Windows.SDK.NET.dll" "WinRT.Runtime.dll")
native_libs=("DesktopBuddyLinuxBridge.so" "libdesktopbuddy_linux_native.so" "libdesktopbuddy_linux_stream.so")

require() { [[ -e "$1" ]] || { echo "Required package input not found: $1" >&2; exit 1; }; }

copy_file() { mkdir -p "$(dirname "$2")"; cp -f "$1" "$2"; }

copy_managed_deps() {
  local target="$1" dep
  for dep in "${managed_deps[@]}"; do
    require "$mod_out/$dep"
    copy_file "$mod_out/$dep" "$target/$dep"
  done
}

copy_native_libs() {
  local target="$1" lib
  for lib in "${native_libs[@]}"; do
    if [[ -f "$native_dir/$lib" ]]; then
      copy_file "$native_dir/$lib" "$target/$lib"
    else
      echo "WARNING: Linux native library missing (Linux support absent from package): $native_dir/$lib" >&2
    fi
  done
}

update_md5_manifest() {
  local rt="$1"
  local out="$rt/DesktopBuddySetupPayloads.md5"
  local p
  : > "$out"
  for p in softcam64.dll softcam.dll VBCABLE_Setup_x64.exe; do
    require "$rt/$p"
    echo "$p=$(md5sum "$rt/$p" | cut -d' ' -f1)" >> "$out"
  done
}

write_manifest() {
  local stage="$1" name="$2" ver="$3" desc="$4"; shift 4
  local deps=("$@")
  local deps_json
  if [[ ${#deps[@]} -eq 0 ]]; then
    deps_json="[]"
  else
    deps_json="$(printf '%s\n' "${deps[@]}" | jq -R . | jq -s .)"
  fi
  jq -n \
    --arg name "$name" \
    --arg version "$ver" \
    --arg website "$website_url" \
    --arg description "$desc" \
    --argjson dependencies "$deps_json" \
    '{name:$name, version_number:$version, website_url:$website, description:$description, dependencies:$dependencies}' \
    > "$stage/manifest.json"
  copy_file "$root/README_THUNDERSTORE.md" "$stage/README.md"
  copy_file "$root/CHANGELOG.md" "$stage/CHANGELOG.md"
}

runtime_icon() {
  # Prefer the committed grayscale icon; fall back to ImageMagick, then to the color icon.
  local dest="$root/icon_runtime.png"
  if [[ -f "$dest" ]]; then printf '%s' "$dest"; return; fi
  if command -v magick >/dev/null 2>&1; then magick "$root/icon.png" -resize 256x256 -colorspace Gray -type Grayscale "$dest" 2>/dev/null || true; fi
  [[ -f "$dest" ]] || cp -f "$root/icon.png" "$dest"
  printf '%s' "$dest"
}

make_zip() {
  local stage="$1" out="$2"
  rm -f "$out"
  mkdir -p "$(dirname "$out")"
  python3 - "$stage" "$out" <<'PY'
import os, sys, zipfile
stage, out = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
    for r, _, files in os.walk(stage):
        for f in sorted(files):
            full = os.path.join(r, f)
            rel = os.path.relpath(full, stage).replace(os.sep, '/')
            z.write(full, rel)
PY
}

new_stage() {
  local name="$1"
  local stage; stage="$(mktemp -d)/$name"
  mkdir -p "$stage"
  printf '%s' "$stage"
}

build_runtime_package() {
  require "$runtime_source"; require "$root/icon.png"; require "$root/README_THUNDERSTORE.md"
  update_md5_manifest "$runtime_source"
  local icon; icon="$(runtime_icon)"
  local name="${namespace}-${runtime_package_name}-${runtime_version}"
  local stage; stage="$(new_stage "$name")"
  local out="$root/build/$name.zip"
  local rt="$stage/plugins/DesktopBuddy/$runtime_dir_name"
  mkdir -p "$rt"
  write_manifest "$stage" "$runtime_package_name" "$runtime_version" "Runtime payloads for DesktopBuddy, including FFmpeg, tunnel, virtual camera, and virtual audio setup files."
  cp -f "$icon" "$stage/icon.png"
  cp -rf "$runtime_source/." "$rt/"
  copy_managed_deps "$rt"
  copy_native_libs "$rt"
  make_zip "$stage" "$out"
  rm -rf "$(dirname "$stage")"
  echo "Done: $out (Thunderstore runtime package)"
}

build_main_package() {
  require "$mod_out/DesktopBuddy.dll"; require "$bridge_dll"; require "$root/icon.png"; require "$root/README_THUNDERSTORE.md"
  local name="${namespace}-${main_name}-${version}"
  local stage; stage="$(new_stage "$name")"
  local out="$root/build/$name.zip"
  local game="$stage/plugins/DesktopBuddy"
  local bridge="$stage/Renderer/BepInEx/plugins/DesktopBuddySharedTextureBridge"
  mkdir -p "$game" "$bridge"
  write_manifest "$stage" "$main_name" "$version" "$description" "${main_dependencies[@]}"
  cp -f "$root/icon.png" "$stage/icon.png"
  copy_file "$mod_out/DesktopBuddy.dll" "$game/DesktopBuddy.dll"
  copy_file "$root/icon_transparent.png" "$game/icon_transparent.png"
  copy_file "$root/scripts/CollectDesktopBuddyDiagnostics.ps1" "$game/CollectDesktopBuddyDiagnostics.ps1"
  [[ -f "$mod_out/DesktopBuddy.sha" ]] && copy_file "$mod_out/DesktopBuddy.sha" "$game/DesktopBuddy.sha"
  copy_file "$bridge_dll" "$bridge/DesktopBuddySharedTextureBridge.dll"
  copy_native_libs "$bridge"
  make_zip "$stage" "$out"
  rm -rf "$(dirname "$stage")"
  echo "Done: $out (Thunderstore main package)"
}

build_manual_package() {
  require "$mod_out/DesktopBuddy.dll"; require "$bridge_dll"; require "$runtime_source"; require "$root/icon_transparent.png"
  update_md5_manifest "$runtime_source"
  local icon; icon="$(runtime_icon)"
  local name="DesktopBuddy-${version}"
  local stage; stage="$(new_stage "$name")"
  local out="$root/$name.zip"
  local main_root="$stage/BepInEx/plugins/${namespace}-${main_name}"
  local rt_root="$stage/BepInEx/plugins/${namespace}-${runtime_package_name}"
  local game="$main_root/DesktopBuddy"
  local rt="$rt_root/DesktopBuddy/$runtime_dir_name"
  local bridge="$stage/Renderer/BepInEx/plugins/${namespace}-${main_name}/DesktopBuddySharedTextureBridge"
  mkdir -p "$game" "$rt" "$bridge"
  write_manifest "$main_root" "$main_name" "$version" "$description" "${main_dependencies[@]}"
  cp -f "$root/icon.png" "$main_root/icon.png"
  write_manifest "$rt_root" "$runtime_package_name" "$runtime_version" "Runtime payloads for DesktopBuddy, including FFmpeg, tunnel, virtual camera, and virtual audio setup files."
  cp -f "$icon" "$rt_root/icon.png"
  copy_file "$mod_out/DesktopBuddy.dll" "$game/DesktopBuddy.dll"
  copy_file "$root/icon_transparent.png" "$game/icon_transparent.png"
  copy_file "$root/scripts/CollectDesktopBuddyDiagnostics.ps1" "$game/CollectDesktopBuddyDiagnostics.ps1"
  [[ -f "$mod_out/DesktopBuddy.sha" ]] && copy_file "$mod_out/DesktopBuddy.sha" "$game/DesktopBuddy.sha"
  cp -rf "$runtime_source/." "$rt/"
  copy_managed_deps "$rt"
  copy_native_libs "$rt"
  copy_file "$bridge_dll" "$bridge/DesktopBuddySharedTextureBridge.dll"
  copy_native_libs "$bridge"
  make_zip "$stage" "$out"
  rm -rf "$(dirname "$stage")"
  echo "Done: $out (manual profile-root package layout)"
}

case "$package" in
  Manual) build_manual_package ;;
  Main) build_main_package ;;
  Runtime) build_runtime_package ;;
  All) build_runtime_package; build_main_package ;;
  *) echo "Unknown package kind: $package" >&2; exit 2 ;;
esac
