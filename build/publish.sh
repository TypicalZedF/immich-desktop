#!/usr/bin/env bash
# Builds a Linux release as a tarball with an install script.
#   ./build/publish.sh 1.2.0
# Output: artifacts/installer/ImmichSplash-linux-x64.tar.gz
# Users extract it and run ./install.sh (installs to ~/.local, adds a menu entry; ./install.sh --uninstall removes it).
set -euo pipefail

VERSION="${1:-1.0.0}"
RUNTIME="${2:-linux-x64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PUBLISH_DIR="$ROOT/artifacts/publish/$RUNTIME"
OUT_DIR="$ROOT/artifacts/installer"
STAGE="$ROOT/artifacts/stage/immich-splash"

dotnet test "$ROOT/tests/ImmichSplash.Core.Tests" -c Release --nologo

rm -rf "$PUBLISH_DIR" "$STAGE"
dotnet publish "$ROOT/src/ImmichSplash.App/ImmichSplash.App.csproj" -c Release -r "$RUNTIME" --self-contained true \
  -p:Version="$VERSION" -p:DebugType=none -o "$PUBLISH_DIR" --nologo

mkdir -p "$STAGE" "$OUT_DIR"
cp -r "$PUBLISH_DIR" "$STAGE/app"
cp "$ROOT/src/ImmichSplash.App/Assets/logo-512.png" "$STAGE/immich-splash.png"
cp "$ROOT/build/linux-install.sh" "$STAGE/install.sh"
chmod +x "$STAGE/install.sh"

tar -C "$ROOT/artifacts/stage" -czf "$OUT_DIR/ImmichSplash-$RUNTIME.tar.gz" immich-splash
echo "Ready: $OUT_DIR/ImmichSplash-$RUNTIME.tar.gz"
