#!/usr/bin/env bash
# Monta o AppImage autossuficiente do SemRastro para Linux.
#
#   packaging/linux/build-appimage.sh <x64|arm64> [versao]
#
# Saida: dist/SemRastro-<versao>-linux-<x86_64|aarch64>.AppImage
#
# Estrutura do AppDir:
#   AppRun                          -> exporta APPDIR e chama usr/lib/semrastro/SemRastro
#   usr/lib/semrastro/              -> dotnet publish self-contained (runtime .NET incluso)
#   usr/bin/ffmpeg                  -> build estatico (johnvansickle, GPLv3)
#   usr/share/semrastro/exiftool/   -> ExifTool (perl puro; usa o perl do sistema)
#   semrastro.desktop, semrastro.png
#
# Precisa de: dotnet SDK, curl, tar, xz, file. O appimagetool e baixado (continuous).
set -euo pipefail
ARCH="$1"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
VERSION="${2:-$(tr -d '[:space:]' < "$ROOT/version.txt")}"
RID="linux-$ARCH"
case "$ARCH" in
  x64)   AIARCH=x86_64;  ;;
  arm64) AIARCH=aarch64; ;;
  *) echo "arquitetura: x64 ou arm64"; exit 1;;
esac
OUTDIR="$ROOT/build/linux/$ARCH"
APPDIR="$OUTDIR/SemRastro.AppDir"
PUB="$ROOT/build/publish/$RID"
DIST="$ROOT/dist"; mkdir -p "$DIST" "$OUTDIR"
OUT="$DIST/SemRastro-$VERSION-linux-$AIARCH.AppImage"

echo "== publish $RID (versao $VERSION)"
rm -rf "$PUB" "$APPDIR"
dotnet publish "$ROOT/src/SemRastro.Desktop" -c Release -r "$RID" --self-contained true \
  -p:Version="$VERSION" -p:UseAppHost=true -p:PublishSingleFile=false -o "$PUB" --nologo -v minimal

echo "== AppDir"
mkdir -p "$APPDIR/usr/lib/semrastro" "$APPDIR/usr/bin" "$APPDIR/usr/share/semrastro" \
         "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/256x256/apps" "$APPDIR/usr/share/doc/semrastro"
cp -R "$PUB"/. "$APPDIR/usr/lib/semrastro/"
chmod +x "$APPDIR/usr/lib/semrastro/SemRastro"
cp "$HERE/AppRun" "$APPDIR/AppRun"; chmod +x "$APPDIR/AppRun"
cp "$HERE/semrastro.desktop" "$APPDIR/semrastro.desktop"
cp "$HERE/semrastro.desktop" "$APPDIR/usr/share/applications/semrastro.desktop"
cp "$ROOT/src/SemRastro.Desktop/Assets/semrastro.png" "$APPDIR/semrastro.png"
cp "$ROOT/src/SemRastro.Desktop/Assets/semrastro.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/semrastro.png"
cp "$ROOT/LICENSE" "$ROOT/THIRD_PARTY_NOTICES.md" "$APPDIR/usr/share/doc/semrastro/"

echo "== ffmpeg + exiftool (dentro do AppDir)"
"$ROOT/packaging/fetch-tools.sh" linux "$ARCH" "$OUTDIR/tools"
cp "$OUTDIR/tools/ffmpeg" "$APPDIR/usr/bin/ffmpeg"
cp "$OUTDIR/tools/ffmpeg-LICENSE.txt" "$APPDIR/usr/share/doc/semrastro/ffmpeg-LICENSE.txt"
cp -R "$OUTDIR/tools/exiftool" "$APPDIR/usr/share/semrastro/exiftool"

echo "== arquiteturas"
file "$APPDIR/usr/lib/semrastro/SemRastro" "$APPDIR/usr/bin/ffmpeg" | sed 's|.*/||'

echo "== appimagetool"
AIT="$ROOT/build/tools-cache/appimagetool-$AIARCH.AppImage"
if [ ! -f "$AIT" ]; then
  mkdir -p "$(dirname "$AIT")"
  curl -fsSL --retry 3 -o "$AIT" "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$AIARCH.AppImage"
  chmod +x "$AIT"
fi
sha256sum "$AIT" | sed 's|  .*|  (appimagetool continuous)|'
# --appimage-extract-and-run: o proprio appimagetool nao precisa de FUSE para rodar aqui.
ARCH="$AIARCH" "$AIT" --appimage-extract-and-run --no-appstream "$APPDIR" "$OUT"
chmod +x "$OUT"
ls -la "$OUT"

echo "== teste rapido sem FUSE: o app acha as ferramentas dentro do AppImage?"
"$OUT" --appimage-extract-and-run --cli --where
