#!/usr/bin/env bash
set -euo pipefail

# Build the DesktopBuddy Linux native libraries from a clean Debian/Ubuntu base image.
# (The managed .NET assemblies are built separately via `dotnet build` / scripts/build.sh.)

export DEBIAN_FRONTEND=noninteractive

apt-get update
apt-get install -y --no-install-recommends \
  build-essential \
  clang \
  libclang-dev \
  pkg-config \
  curl \
  ca-certificates \
  libavformat-dev \
  libavcodec-dev \
  libswscale-dev \
  libavutil-dev \
  libpipewire-0.3-dev \
  libxkbcommon-dev \
  libwayland-dev

# Rust toolchain (edition 2024 requires Rust >= 1.85). Skip if already present.
if ! command -v cargo >/dev/null 2>&1; then
  curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y --default-toolchain stable --profile minimal
  # shellcheck disable=SC1091
  . "$HOME/.cargo/env"
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
"$root/scripts/build-native.sh" Release
