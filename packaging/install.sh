#!/usr/bin/env bash
# LuaTools Nova — system installer. Usage: sudo ./install.sh [source-dir]
# Installs to /opt/luatools-nova, drops a .desktop entry + hicolor icons,
# registers the luatools:// protocol, and refreshes the desktop database.
set -euo pipefail

SRC_DIR="${1:-artifacts/luatools-nova-linux-x64}"
DEST=/opt/luatools-nova
ICONS_DIR=/usr/share/icons/hicolor

if [ "$(id -u)" -ne 0 ]; then
  echo "Run with sudo: sudo ./install.sh" >&2
  exit 1
fi

if [ ! -x "$SRC_DIR/luatools-nova" ] && [ ! -f "$SRC_DIR/luatools-nova.dll" ]; then
  echo "Build first: ./packaging/publish.sh  (missing $SRC_DIR/luatools-nova)" >&2
  exit 1
fi

echo "→ Installing to $DEST"
mkdir -p "$DEST"
cp -a "$SRC_DIR/." "$DEST/"

echo "→ Desktop entry"
install -Dm644 packaging/luatools-nova.desktop /usr/share/applications/luatools-nova.desktop

echo "→ Icons"
for spec in 16:16 24:24 32:32 48:48 64:64 128:128 256:256; do
  size="${spec%%:*}"
  src="assets/icon/luatools-nova_${size}.png"
  [ -f "$src" ] && install -Dm644 "$src" "$ICONS_DIR/${size}x${size}/apps/luatools-nova.png" || true
done
install -Dm644 assets/icon/com.luatools.nova.png "$ICONS_DIR/128x128/apps/luatools-nova.png" || true

echo "→ Refreshing caches"
update-desktop-database >/dev/null 2>&1 || true
gtk-update-icon-cache -f "$ICONS_DIR" >/dev/null 2>&1 || true

echo "✓ Installed. Launch 'LuaTools Nova' from your app menu or run: $DEST/luatools-nova"
