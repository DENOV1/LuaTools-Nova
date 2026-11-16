#!/usr/bin/env bash
# LuaTools Nova — publish script: builds a self-contained linux-x64 bundle.
set -euo pipefail
cd "$(dirname "$0")/.."

# Prefer the workspace-local SDK (sandbox), fall back to PATH.
if [ -z "${DOTNET:-}" ] && [ -x /home/z/my-project/tools/dotnet/dotnet ]; then
  DOTNET=/home/z/my-project/tools/dotnet/dotnet
fi
DOTNET=${DOTNET:-dotnet}
OUT=artifacts/luatools-nova-linux-x64

echo "== Building + testing =="
"$DOTNET" test LuaTools.Nova.sln -c Release --nologo -v q

echo "== Publishing self-contained linux-x64 =="
"$DOTNET" publish src/LuaTools.UI -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=false -o "$OUT"

echo "== Staging packaging files =="
cp packaging/luatools-nova.desktop "$OUT/"
cp assets/icon/luatools-nova_16.png "$OUT/"
cp assets/icon/luatools-nova_256.png "$OUT/"
cp assets/icon/luatools-nova.ico "$OUT/"

echo "OK → $OUT/luatools-nova"
