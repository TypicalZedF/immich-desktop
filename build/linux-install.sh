#!/usr/bin/env bash
# Installs (or updates) Immich Splash from the extracted tarball into ~/.local, with a menu entry.
#   ./install.sh               install or update
#   ./install.sh --uninstall   remove it (settings stay in ~/.local/share/immich-splash)
# Shipped in ImmichSplash-linux-x64.tar.gz (build/publish.sh) and used by build/dev.ps1 -Linux.
set -euo pipefail
DEST="$HOME/.local/opt/immich-splash"
APPS="$HOME/.local/share/applications"
ICONS="$HOME/.local/share/icons/hicolor/512x512/apps"
if [[ "${1:-}" == "--uninstall" ]]; then
  "$DEST/ImmichDesktop" --uninstall || true
  rm -rf "$DEST" "$APPS/immich-splash.desktop" "$ICONS/immich-splash.png"
  echo "Immich Splash removed. Settings remain in ~/.local/share/immich-splash (delete it to remove them)."
  exit 0
fi
HERE="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$DEST" "$APPS" "$ICONS"
"$DEST/ImmichDesktop" --quit 2>/dev/null || true
cp -r "$HERE/app/." "$DEST/"
chmod +x "$DEST/ImmichDesktop"
cp "$HERE/immich-splash.png" "$ICONS/immich-splash.png"
cat > "$APPS/immich-splash.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Immich Splash
Comment=Back up photos and videos to Immich
Exec="$DEST/ImmichDesktop" %u
Icon=immich-splash
Categories=Graphics;Photography;Utility;
MimeType=x-scheme-handler/app.immich;
Terminal=false
DESKTOP
update-desktop-database "$APPS" 2>/dev/null || true
echo "Immich Splash installed. Find it in your applications menu."
