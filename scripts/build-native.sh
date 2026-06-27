#!/usr/bin/env bash
set -euo pipefail

# Builds the DesktopBuddy Linux native libraries:
#   - libdesktopbuddy_linux_native.so  (Rust cdylib: portal capture/input)
#   - DesktopBuddyLinuxBridge.so       (C: bridge loader)
#   - libdesktopbuddy_linux_stream.so  (C: FFmpeg native stream)
# Requires: cargo, cc, pkg-config, and FFmpeg dev libraries on PATH.

configuration="${1:-${CONFIGURATION:-Release}}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
out="$root/DesktopBuddyLinuxBridge/bin/$configuration"

for tool in cargo cc pkg-config; do
  command -v "$tool" >/dev/null 2>&1 || { echo "Required tool '$tool' not found on PATH." >&2; exit 127; }
done

mkdir -p "$out"

echo "Building Linux native capture bridge ($configuration)"
cargo build --manifest-path "$root/DesktopBuddyLinuxNative/Cargo.toml" --release

cc -shared -fPIC "$root/DesktopBuddyLinuxBridge/desktopbuddy_linux_bridge.c" \
  -o "$out/DesktopBuddyLinuxBridge.so" \
  -ldl

# word-splitting of pkg-config output into separate cc flags is intentional
# shellcheck disable=SC2046
cc -shared -fPIC "$root/DesktopBuddyLinuxNative/src/native_stream.c" \
  -o "$out/libdesktopbuddy_linux_stream.so" \
  $(pkg-config --cflags libavformat libavcodec libswscale libavutil) \
  -ldl -lpthread

cp -f \
  "$root/DesktopBuddyLinuxNative/target/release/libdesktopbuddy_linux_native.so" \
  "$out/libdesktopbuddy_linux_native.so"

echo "Native libraries built in: $out"
