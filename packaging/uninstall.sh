#!/usr/bin/env bash
# LuaTools Nova — system uninstaller. Usage: sudo ./uninstall.sh
set -euo pipefail

DEST="${DEST_OVERRIDE:-/opt/luatools-nova}"
if [ -n "${DEST_OVERRIDE:-}" ]; then
  APPS_DIR="${APPS_DIR_OVERRIDE:-$HOME/.local/share/applications}"
  ICONS_DIR="${ICONS_DIR_OVERRIDE:-$HOME/.local/share/icons/hicolor}"
  PRIV=0
else
  APPS_DIR=/usr/share/applications
  ICONS_DIR=/usr/share/icons/hicolor
  PRIV=1
fi

if [ "$PRIV" -eq 1 ] && [ "$(id -u)" -ne 0 ]; then
  echo "Run with sudo: sudo ./uninstall.sh   (or: DEST_OVERRIDE=~/... ./uninstall.sh)" >&2
  exit 1
fi

echo "→ Removing $DEST"
rm -rf "$DEST"

echo "→ Removing desktop entry + icons"
rm -f "$APPS_DIR/luatools-nova.desktop"
for size in 16 24 32 48 64 128 256; do
  rm -f "$ICONS_DIR/${size}x${size}/apps/luatools-nova.png"
done

echo "→ User data left in place (remove manually if desired):"
echo "    ~/.config/LuaToolsNova  ~/.local/share/LuaToolsNova"

echo "→ Refreshing caches"
update-desktop-database >/dev/null 2>&1 || true
gtk-update-icon-cache -f "$ICONS_DIR" >/dev/null 2>&1 || true

echo "✓ Removed."
