#!/usr/bin/env bash
# Baixa (com cache) e verifica os binarios de terceiros listados em tools.lock,
# e monta uma pasta pronta para ir dentro do pacote:
#
#   packaging/fetch-tools.sh <macos|linux> <arm64|x64> <dir-de-saida>
#
# Saida:  <dir>/ffmpeg               (executavel)
#         <dir>/exiftool/exiftool    (script perl) + lib/ + LICENSE
#         <dir>/ffmpeg-LICENSE.txt   (texto GPL do build)
set -euo pipefail
OS="$1"; ARCH="$2"; OUT="$3"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
CACHE="${SEMRASTRO_TOOLS_CACHE:-$ROOT/build/tools-cache}"
mkdir -p "$CACHE" "$OUT"

lock_get() { awk -v k="$1" '$1==k {print $2, $3}' "$HERE/tools.lock"; }

fetch() { # key -> caminho do arquivo em cache, verificado
  local key="$1" url sha file
  read -r url sha < <(lock_get "$key")
  [ -n "$url" ] || { echo "chave $key nao esta em tools.lock" >&2; exit 1; }
  file="$CACHE/$key-${url##*/}"; file="${file%/download}"
  case "$url" in *.tar.gz/download) file="$CACHE/$key.tar.gz";; *.tar.xz) file="$CACHE/$key.tar.xz";; *.zip) file="$CACHE/$key.zip";; esac
  if [ ! -f "$file" ]; then
    echo "baixando $key ..." >&2
    curl -fsSL --retry 3 -o "$file.part" "$url" && mv "$file.part" "$file"
  fi
  local got
  got=$(sha256sum "$file" | cut -d' ' -f1) 2>/dev/null || got=$(shasum -a 256 "$file" | cut -d' ' -f1)
  if [ "$got" != "$sha" ]; then echo "SHA-256 de $key nao confere: $got != $sha" >&2; rm -f "$file"; exit 1; fi
  echo "$file"
}

# ---- ffmpeg ----
FFKEY="ffmpeg-$OS-$ARCH"
FFFILE=$(fetch "$FFKEY")
TMP=$(mktemp -d)
case "$FFFILE" in
  *.zip)    unzip -q -o "$FFFILE" -d "$TMP"; FFBIN=$(find "$TMP" -type f -name ffmpeg | head -1);;
  *.tar.xz) tar -xJf "$FFFILE" -C "$TMP"; FFBIN=$(find "$TMP" -type f -name ffmpeg | head -1)
            LIC=$(find "$TMP" -type f -name 'GPLv3.txt' | head -1); [ -n "$LIC" ] && cp "$LIC" "$OUT/ffmpeg-LICENSE.txt";;
esac
[ -n "$FFBIN" ] || { echo "ffmpeg nao encontrado no pacote $FFFILE" >&2; exit 1; }
cp "$FFBIN" "$OUT/ffmpeg"; chmod +x "$OUT/ffmpeg"
if [ ! -f "$OUT/ffmpeg-LICENSE.txt" ]; then
  # o zip do martin-riedl traz so o binario; o build e GPL (x264). Guarda o texto da GPL.
  curl -fsSL --retry 3 -o "$OUT/ffmpeg-LICENSE.txt" https://www.gnu.org/licenses/gpl-3.0.txt || cp "$ROOT/THIRD_PARTY_NOTICES.md" "$OUT/ffmpeg-LICENSE.txt"
fi

# ---- exiftool ----
ETFILE=$(fetch exiftool)
rm -rf "$OUT/exiftool"; mkdir -p "$OUT/exiftool"
tar -xzf "$ETFILE" -C "$TMP"
ETDIR=$(find "$TMP" -maxdepth 1 -type d -name 'Image-ExifTool-*' | head -1)
cp "$ETDIR/exiftool" "$OUT/exiftool/exiftool"
cp -R "$ETDIR/lib" "$OUT/exiftool/lib"
cp "$ETDIR/README" "$OUT/exiftool/LICENSE"      # "same terms as Perl itself" (Artistic / GPL)
chmod +x "$OUT/exiftool/exiftool"
rm -rf "$TMP"

echo "ferramentas em $OUT:"; ls -la "$OUT" "$OUT/exiftool" | head -20
