#!/usr/bin/env bash
# Monta o SemRastro.app (autossuficiente) para uma arquitetura, com assinatura AD HOC.
#
#   packaging/macos/build-app.sh <arm64|x64> [versao]
#
# Saida: build/macos/<arch>/SemRastro.app
#
# Roda em macOS (precisa de codesign, iconutil). O dotnet publish e cross: um
# runner arm64 gera tambem o build x64.
#
# Assinatura: "codesign --sign -" (ad hoc). Nao procura certificado Developer ID,
# nao notariza, nao fala com a Apple. A assinatura ad hoc so garante a
# integridade do bundle e permite rodar em Apple Silicon; o Gatekeeper vai
# tratar o app como "de desenvolvedor nao identificado" (ver README).
set -euo pipefail
ARCH="$1"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
VERSION="${2:-$(tr -d '[:space:]' < "$ROOT/version.txt")}"
RID="osx-$ARCH"
OUTDIR="$ROOT/build/macos/$ARCH"
APP="$OUTDIR/SemRastro.app"
PUB="$ROOT/build/publish/$RID"

echo "== publish $RID (versao $VERSION)"
rm -rf "$PUB" "$APP"
dotnet publish "$ROOT/src/SemRastro.Desktop" -c Release -r "$RID" --self-contained true \
  -p:Version="$VERSION" -p:UseAppHost=true -p:PublishSingleFile=false -o "$PUB" --nologo -v minimal

echo "== bundle"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources/bin"
cp -R "$PUB"/. "$APP/Contents/MacOS/"
sed "s/__VERSION__/$VERSION/g" "$HERE/Info.plist" > "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"

echo "== icone"
iconutil -c icns "$ROOT/assets/SemRastro.iconset" -o "$APP/Contents/Resources/SemRastro.icns"

echo "== ffmpeg + exiftool (dentro do bundle)"
"$ROOT/packaging/fetch-tools.sh" macos "$ARCH" "$OUTDIR/tools"
cp "$OUTDIR/tools/ffmpeg" "$APP/Contents/Resources/bin/ffmpeg"
cp "$OUTDIR/tools/ffmpeg-LICENSE.txt" "$APP/Contents/Resources/ffmpeg-LICENSE.txt"
cp -R "$OUTDIR/tools/exiftool" "$APP/Contents/Resources/exiftool"
cp "$ROOT/THIRD_PARTY_NOTICES.md" "$ROOT/LICENSE" "$APP/Contents/Resources/"

echo "== arquiteturas"
file "$APP/Contents/MacOS/SemRastro" "$APP/Contents/Resources/bin/ffmpeg" | sed 's|.*/||'
for dylib in "$APP"/Contents/MacOS/*.dylib; do
  case "$(file "$dylib")" in *"$ARCH"*|*x86_64*|*arm64*) ;; *) echo "AVISO: $dylib de arquitetura inesperada";; esac
done | head -5

echo "== assinatura ad hoc (sem Developer ID, sem notarizacao)"
# Assina cada Mach-O e depois o bundle. Sem --options runtime: o .NET usa JIT e
# o hardened runtime exigiria entitlements; ad hoc + hardened nao traz ganho aqui.
# O codesign recusa arquivos com atributos estendidos ("detritus"): limpa antes.
xattr -cr "$APP" 2>/dev/null || true
# Tudo em Contents/MacOS conta como codigo para o codesign, inclusive os .dll
# gerenciados do .NET (nao sao Mach-O, mas o bundle nao valida se ficarem sem
# assinatura). Fora dessa pasta, so os Mach-O (ffmpeg).
find "$APP/Contents" -type f -print0 | while IFS= read -r -d '' f; do
  case "$f" in *"/MacOS/SemRastro") continue;; esac      # o executavel principal vai com o bundle
  case "$f" in
    *"/Contents/MacOS/"*) codesign --force --sign - "$f" 2>&1 | grep -v 'replacing existing signature' || true;;
    *) if file -b "$f" | grep -q 'Mach-O'; then codesign --force --sign - "$f" 2>&1 | grep -v 'replacing existing signature' || true; fi;;
  esac
done
codesign --force --sign - "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"
echo "codesign: ok (ad hoc)"

echo "== teste rapido: o app acha as ferramentas dentro do bundle?"
"$APP/Contents/MacOS/SemRastro" --cli --where
du -sh "$APP"
