#!/usr/bin/env bash
# Installs (or updates) Immich Desktop from the extracted tarball into ~/.local, with a menu entry.
#   ./install.sh               install or update
#   ./install.sh --uninstall   remove it (settings stay in ~/.local/share/immich-desktop)
# Shipped in ImmichDesktop-linux-x64.tar.gz (build/publish.sh) and used by build/dev.ps1 -Linux.
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
