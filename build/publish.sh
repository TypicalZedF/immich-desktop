#!/usr/bin/env bash
# Builds a Linux release and packages it as an AppImage with Velopack.
#   ./build/publish.sh 1.0.0
# Output: artifacts/releases/linux-x64/ImmichDesktop.AppImage
set -euo pipefail

VERSION="${1:-1.0.0}"
RUNTIME="${2:-linux-x64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PUBLISH_DIR="$ROOT/artifacts/publish/$RUNTIME"
RELEASE_DIR="$ROOT/artifacts/releases/$RUNTIME"
VPK_VERSION="1.2.161"

dotnet test "$ROOT/tests/ImmichDesktop.Core.Tests" -c Release --nologo

rm -rf "$PUBLISH_DIR"
dotnet publish "$ROOT/src/ImmichDesktop.App/ImmichDesktop.App.csproj" -c Release -r "$RUNTIME" --self-contained true \
  -p:Version="$VERSION" -p:PublishReadyToRun=true -p:DebugType=none -o "$PUBLISH_DIR" --nologo

command -v vpk >/dev/null || { dotnet tool install -g vpk --version "$VPK_VERSION"; export PATH="$PATH:$HOME/.dotnet/tools"; }

vpk pack \
  --packId ImmichDesktop \
  --packVersion "$VERSION" \
  --packDir "$PUBLISH_DIR" \
  --mainExe ImmichDesktop \
  --packTitle "Immich Desktop" \
  --icon "$ROOT/src/ImmichDesktop.App/Assets/logo-512.png" \
  --runtime "$RUNTIME" \
  --outputDir "$RELEASE_DIR"

echo "AppImage ready in $RELEASE_DIR"
