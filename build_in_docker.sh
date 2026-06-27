#!/bin/bash
set -e

apt-get update
apt-get install -y gcc pkg-config libavformat-dev libavcodec-dev libswscale-dev libavutil-dev

cargo build --manifest-path DesktopBuddyLinuxNative/Cargo.toml --release

mkdir -p DesktopBuddyLinuxBridge/bin/Release

cc -shared -fPIC DesktopBuddyLinuxBridge/desktopbuddy_linux_bridge.c \
  -o DesktopBuddyLinuxBridge/bin/Release/DesktopBuddyLinuxBridge.so \
  -ldl

cc -shared -fPIC DesktopBuddyLinuxNative/src/native_stream.c \
  -o DesktopBuddyLinuxBridge/bin/Release/libdesktopbuddy_linux_stream.so \
  $(pkg-config --cflags libavformat libavcodec libswscale libavutil) \
  -ldl -lpthread

cp -f DesktopBuddyLinuxNative/target/release/libdesktopbuddy_linux_native.so DesktopBuddyLinuxBridge/bin/Release/libdesktopbuddy_linux_native.so
