#!/usr/bin/env bash
# Empacota build/macos/<arch>/SemRastro.app num .dmg de "arrastar para Aplicativos".
#
#   packaging/macos/build-dmg.sh <arm64|x64> [versao]
#
# Saida: dist/SemRastro-<versao>-macos-<arm64|x64>.dmg
#
# Usa create-dmg (brew install create-dmg) se existir, para posicionar os icones
# e o fundo com a seta; senao cai no hdiutil puro (funciona igual, sem layout).
# Nenhum dos dois assina ou notariza: as opcoes --codesign/--notarize do
# create-dmg nunca sao passadas.
set -euo pipefail
ARCH="$1"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
VERSION="${2:-$(tr -d '[:space:]' < "$ROOT/version.txt")}"
APP="$ROOT/build/macos/$ARCH/SemRastro.app"
[ -d "$APP" ] || { echo "rode build-app.sh $ARCH antes"; exit 1; }
DIST="$ROOT/dist"; mkdir -p "$DIST"
DMG="$DIST/SemRastro-$VERSION-macos-$ARCH.dmg"
STAGE="$ROOT/build/macos/$ARCH/dmg-stage"
rm -rf "$STAGE" "$DMG"; mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/SemRastro.app"
ln -s /Applications "$STAGE/Applications"
cp "$HERE/LEIA-ME.txt" "$STAGE/LEIA-ME.txt"

if command -v create-dmg >/dev/null 2>&1; then
  echo "== create-dmg"
  create-dmg \
    --volname "SemRastro $VERSION" \
    --background "$HERE/dmg-background.png" \
    --window-pos 200 120 --window-size 600 400 \
    --icon-size 110 \
    --icon "SemRastro.app" 150 175 \
    --app-drop-link 450 175 \
    --icon "LEIA-ME.txt" 300 320 \
    --hide-extension "SemRastro.app" \
    --no-internet-enable \
    "$DMG" "$STAGE" || { echo "create-dmg falhou; usando hdiutil"; rm -f "$DMG"; }
fi
if [ ! -f "$DMG" ]; then
  echo "== hdiutil"
  hdiutil create -volname "SemRastro $VERSION" -srcfolder "$STAGE" -ov -format UDZO "$DMG"
fi

echo "== verificacao: monta, confere conteudo, desmonta"
MNT=$(mktemp -d)
hdiutil attach "$DMG" -mountpoint "$MNT" -nobrowse -quiet
ls -la "$MNT"
[ -d "$MNT/SemRastro.app" ] && [ -L "$MNT/Applications" ] && echo "dmg ok: .app + atalho Applications"
hdiutil detach "$MNT" -quiet
ls -la "$DMG"
