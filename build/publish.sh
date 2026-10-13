#!/usr/bin/env bash
# Builds a Linux release as a tarball with an install script.
#   ./build/publish.sh 1.2.0
# Output: artifacts/installer/ImmichDesktop-linux-x64.tar.gz
# Users extract it and run ./install.sh (installs to ~/.local, adds a menu entry; ./install.sh --uninstall removes it).
set -euo pipefail

VERSION="${1:-1.0.0}"
RUNTIME="${2:-linux-x64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PUBLISH_DIR="$ROOT/artifacts/publish/$RUNTIME"
OUT_DIR="$ROOT/artifacts/installer"
STAGE="$ROOT/artifacts/stage/immich-desktop"

dotnet test "$ROOT/tests/ImmichDesktop.Core.Tests" -c Release --nologo

rm -rf "$PUBLISH_DIR" "$STAGE"
dotnet publish "$ROOT/src/ImmichDesktop.App/ImmichDesktop.App.csproj" -c Release -r "$RUNTIME" --self-contained true \
  -p:Version="$VERSION" -p:DebugType=none -o "$PUBLISH_DIR" --nologo

mkdir -p "$STAGE" "$OUT_DIR"
cp -r "$PUBLISH_DIR" "$STAGE/app"
cp "$ROOT/src/ImmichDesktop.App/Assets/logo-512.png" "$STAGE/immich-desktop.png"
cat > "$STAGE/install.sh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
DEST="$HOME/.local/opt/immich-desktop"
APPS="$HOME/.local/share/applications"
ICONS="$HOME/.local/share/icons/hicolor/512x512/apps"
if [[ "${1:-}" == "--uninstall" ]]; then
  "$DEST/ImmichDesktop" --uninstall || true
  rm -rf "$DEST" "$APPS/immich-desktop.desktop" "$ICONS/immich-desktop.png"
  echo "Immich Desktop removed. Settings remain in ~/.local/share/immich-desktop (delete it to remove them)."
  exit 0
fi
HERE="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$DEST" "$APPS" "$ICONS"
"$DEST/ImmichDesktop" --quit 2>/dev/null || true
cp -r "$HERE/app/." "$DEST/"
chmod +x "$DEST/ImmichDesktop"
cp "$HERE/immich-desktop.png" "$ICONS/immich-desktop.png"
cat > "$APPS/immich-desktop.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Immich Desktop
Comment=Back up photos and videos to Immich
Exec="$DEST/ImmichDesktop" %u
Icon=immich-desktop
Categories=Graphics;Photography;Utility;
MimeType=x-scheme-handler/app.immich;
Terminal=false
DESKTOP
update-desktop-database "$APPS" 2>/dev/null || true
echo "Immich Desktop installed. Find it in your applications menu."
EOF
chmod +x "$STAGE/install.sh"

tar -C "$ROOT/artifacts/stage" -czf "$OUT_DIR/ImmichDesktop-$RUNTIME.tar.gz" immich-desktop
echo "Ready: $OUT_DIR/ImmichDesktop-$RUNTIME.tar.gz"
