#!/usr/bin/env bash
# Bateria de remocao de metadados para macOS/Linux, contra o PACOTE final:
# planta valores conhecidos, roda o pipeline real ("SemRastro --cli", o mesmo
# codigo da janela) e procura os valores na saida com o exiftool e por busca
# binaria crua. Confere tambem que o original nao muda (SHA-256) e que caminhos
# com espacos e acentos funcionam.
#
#   tests/run_tests_unix.sh <comando do app...>
#   ex.: tests/run_tests_unix.sh build/macos/arm64/SemRastro.app/Contents/MacOS/SemRastro
#        tests/run_tests_unix.sh dist/SemRastro-1.0.0-linux-x86_64.AppImage --appimage-extract-and-run
#
# Precisa de: python3 (para o chunk PNG), sha256sum ou shasum.
set -u
APP=("$@")
[ ${#APP[@]} -gt 0 ] || { echo "uso: $0 <app> [args]"; exit 64; }
# O script troca de pasta mais abaixo: qualquer argumento que seja um arquivo
# existente (o binario, o AppImage, mesmo depois de "arch -x86_64") vira absoluto.
for i in "${!APP[@]}"; do
  if [ -e "${APP[$i]}" ]; then APP[$i]="$(cd "$(dirname "${APP[$i]}")" && pwd)/$(basename "${APP[$i]}")"; fi
done

# ferramentas de inspecao: as mesmas embutidas no pacote (exiftool le; ffmpeg gera as amostras)
eval "$("${APP[@]}" --cli --where | sed "s/^\([a-z]*\)=\(.*\)$/T_\1='\2'/")"   # valores entre aspas: caminhos com espaco
FF="$T_ffmpeg"; ET_SCRIPT="$T_exiftool"; PERL="$T_perl"
[ -x "$FF" ] || { echo "ffmpeg nao localizado pelo app: '$FF'"; exit 1; }
[ -f "$ET_SCRIPT" ] || { echo "exiftool nao localizado pelo app"; exit 1; }
ET() { "$PERL" "$ET_SCRIPT" "$@"; }
sha() { (sha256sum "$1" 2>/dev/null || shasum -a 256 "$1") | cut -d' ' -f1; }

T="$(mktemp -d)/semrastro tést ção"   # caminho com espaco e acentos, de proposito
mkdir -p "$T"; cd "$T"
echo "pasta de teste: $T"
echo "ffmpeg: $FF"; echo "exiftool: $ET_SCRIPT (perl $PERL)"

PLANTED=(TITULO_SECRETO ARTISTA_SECRETO "Rua Augusta" STREAM_VIDEO_TITULO STREAM_AUDIO_TITULO
  HANDLER_SECRETO SOFTWARE_SECRETO COPYRIGHT_SECRETO iPhone Apple 23.5613 46.6565
  CAPITULO_SECRETO LEGENDA_SECRETA LIXO_FINAL_SECRETO "2024:05:06" LENTE_SECRETA
  DESCRICAO_SECRETA COMENTARIO_SECRETO BYLINE_SECRETO CIDADE_SECRETA PALAVRA_SECRETA
  CRIADOR_SECRETO DIREITOS_SECRETOS ASSUNTO_SECRETO COMPUTADOR_SECRETO DONO_SECRETO
  SERIAL123456 THUMB_SECRETA DEPOIS_DO_EOI_SECRETO CHUNK_PRIVADO_SECRETO Lavf Lavc x264)
SENS='GPS|Location|Latitude|Longitude|Make|Model|Device|Serial|Camera|CreateDate|CreationDate|CreationTime|ModifyDate|Software|Encoder|Author|Artist|Owner|Title|Comment|Description|Copyright|Lens|Creator|By-line|Credit|Keywords|Subject|City|Country|Rights|HostComputer|UserComment|ImageDescription|Thumbnail|Language'
FAILS=0; PASS=0

count_strings() { local n=0 hits=""; for s in "${PLANTED[@]}"; do if LC_ALL=C grep -aqF -- "$s" "$1"; then n=$((n+1)); hits="$hits $s"; fi; done; echo "$n/${#PLANTED[@]}:$hits"; }

run_case() { # nome entrada modo [strings-toleradas]
  local name="$1" in="$2" idx="$3" allow="${4:-}"
  echo "=================================================================="
  echo "CASO: $name  (modo $idx)"
  local before; before=$(sha "$in")
  echo "  entrada: $(count_strings "$in")"
  local out; out=$("${APP[@]}" --cli "$in" "$idx" 2>"$T/last.log")
  if [ -z "$out" ] || [ "$out" = "FAIL" ]; then echo "  >>> FALHOU: sem saida"; tail -5 "$T/last.log" | sed 's/^/     /'; FAILS=$((FAILS+1)); return; fi
  echo "  saida: $(basename "$out") ($(stat -c %s "$out" 2>/dev/null || stat -f %z "$out") bytes)"
  local res; res=$(count_strings "$out"); echo "  saida:   $res"
  local hits="${res#*:}"; hits="${hits# }"
  local ok=1
  if [ -n "$hits" ] && [ "$hits" != "$allow" ]; then echo "  >>> FALHOU: sobrou '$hits'"; ok=0; fi
  local sens; sens=$(ET -G1 -a -s "$out" | grep -E "$SENS" | grep -vE 'HandlerDescription|0000:00:00|MediaLanguageCode|FileModifyDate|FileAccessDate|FileInodeChangeDate|FileCreateDate' || true)
  if [ -n "$sens" ]; then echo "  >>> FALHOU: exiftool ainda ve:"; echo "$sens" | sed 's/^/     /'; ok=0; fi
  if [ "$(sha "$in")" != "$before" ]; then echo "  >>> FALHOU: o ORIGINAL mudou"; ok=0; fi
  # decodifica a saida inteira: precisa continuar legivel
  if ! "$FF" -v error -i "$out" -f null - 2>"$T/dec.log"; then echo "  >>> FALHOU: saida nao decodifica"; head -3 "$T/dec.log"; ok=0; fi
  if [ $ok = 1 ]; then PASS=$((PASS+1)); echo "  ok"; else FAILS=$((FAILS+1)); fi
  echo "$out" > "$T/last_out"
}

# ------------------------------------------------------------ VIDEO
printf ';FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=1000\ntitle=CAPITULO_SECRETO\n' > chap.txt
printf '1\n00:00:00,000 --> 00:00:01,000\nLEGENDA_SECRETA\n' > sub.srt
"$FF" -v error -y -f lavfi -i testsrc=size=320x240:rate=25:duration=2 -f lavfi -i sine=frequency=440:duration=2 -i chap.txt -i sub.srt \
  -map 0:v -map 1:a -map 3:s -map_metadata 2 -c:v libx264 -pix_fmt yuv420p -c:a aac -c:s mov_text \
  -metadata title=TITULO_SECRETO -metadata artist=ARTISTA_SECRETO -metadata comment="Rua Augusta 1500" -metadata copyright=COPYRIGHT_SECRETO \
  -metadata:s:v:0 title=STREAM_VIDEO_TITULO -metadata:s:v:0 handler_name=HANDLER_SECRETO -metadata:s:a:0 title=STREAM_AUDIO_TITULO \
  "vídeo cheio.mp4"
ET -q -overwrite_original -Keys:GPSCoordinates="-23.5613, -46.6565" -Keys:Make=Apple -Keys:Model="iPhone 15 Pro Max" \
  -Keys:Software=SOFTWARE_SECRETO -Keys:CreationDate="2024:05:06 10:11:12-03:00" -Keys:Description=DESCRICAO_SECRETA "vídeo cheio.mp4"
printf 'LIXO_FINAL_SECRETO' >> "vídeo cheio.mp4"
run_case "video completo (CRF 18)" "$T/vídeo cheio.mp4" 1 "Lavc"

"$FF" -v error -y -f lavfi -i testsrc=size=321x241:rate=25:duration=1 -c:v libx264rgb impar.mp4
run_case "video dimensoes impares" "$T/impar.mp4" 2

"$FF" -v error -y -f lavfi -i testsrc=size=320x240:rate=25:duration=1 -c:v libx264 -pix_fmt yuv420p par.mp4
"$FF" -v error -y -display_rotation 90 -i par.mp4 -c copy rot.mp4
run_case "video rotacionado" "$T/rot.mp4" 2
rot=$(ET -ImageWidth -ImageHeight -Rotation -s -S "$(cat "$T/last_out")" | tr -d '\r' | tr '\n' ' ')
echo "  dimensoes/rotacao da saida: $rot"
case "$rot" in "240 320 0 ") ;; *) echo "  >>> FALHOU: rotacao nao aplicada fisicamente"; FAILS=$((FAILS+1));; esac

# ------------------------------------------------------------ IMAGENS
"$FF" -v error -y -f lavfi -i testsrc=size=160x120 -frames:v 1 -update 1 thumb.jpg; ET -q -overwrite_original -Comment=THUMB_SECRETA thumb.jpg
for f in jpg png tif bmp; do "$FF" -v error -y -f lavfi -i testsrc=size=640x480 -frames:v 1 -update 1 "base.$f"; done
"$FF" -v error -y -f lavfi -i testsrc=size=640x480 -frames:v 1 -update 1 -c:v libwebp base.webp
"$FF" -v error -y -f lavfi -i testsrc=size=160x120:rate=5:duration=1 base.gif
EXIFARGS=(-Make=Apple -Model="iPhone 15 Pro Max" -LensModel=LENTE_SECRETA -Software=SOFTWARE_SECRETO -Artist=ARTISTA_SECRETO
  -Copyright=COPYRIGHT_SECRETO -ImageDescription=DESCRICAO_SECRETA -UserComment=COMENTARIO_SECRETO
  -GPSLatitude=23.5613 -GPSLatitudeRef=S -GPSLongitude=46.6565 -GPSLongitudeRef=W -HostComputer=COMPUTADOR_SECRETO
  -OwnerName=DONO_SECRETO -SerialNumber=SERIAL123456 -DateTimeOriginal="2024:05:06 10:11:12"
  -XMP:Creator=CRIADOR_SECRETO -XMP:Rights=DIREITOS_SECRETOS -XMP:Subject=ASSUNTO_SECRETO)
IPTCARGS=(-IPTC:By-line=BYLINE_SECRETO -IPTC:City=CIDADE_SECRETA -IPTC:Keywords=PALAVRA_SECRETA)

cp base.jpg "foto açaí.jpg"; ET -q -overwrite_original "${EXIFARGS[@]}" "${IPTCARGS[@]}" "-ThumbnailImage<=thumb.jpg" "foto açaí.jpg"; printf 'DEPOIS_DO_EOI_SECRETO' >> "foto açaí.jpg"
cp base.png img.png; ET -q -overwrite_original "${EXIFARGS[@]}" -PNG:Title=TITULO_SECRETO -PNG:Comment=COMENTARIO_SECRETO img.png
python3 - img.png <<'EOF'
import struct, zlib, sys
p=sys.argv[1]; d=open(p,'rb').read(); i=d.rfind(b'IEND')-4; body=b'CHUNK_PRIVADO_SECRETO'
c=struct.pack('>I',len(body))+b'prVt'+body+struct.pack('>I',zlib.crc32(b'prVt'+body)&0xffffffff)
open(p,'wb').write(d[:i]+c+d[i:])
EOF
cp base.webp img.webp; ET -q -overwrite_original "${EXIFARGS[@]}" img.webp
cp base.tif img.tif;   ET -q -overwrite_original "${EXIFARGS[@]}" "${IPTCARGS[@]}" img.tif
cp base.gif img.gif;   ET -q -overwrite_original -Comment=COMENTARIO_SECRETO -XMP:Creator=CRIADOR_SECRETO img.gif
cp base.bmp img.bmp

run_case "JPG recodificar (caminho com acento)" "$T/foto açaí.jpg" 1
run_case "JPG sem perda" "$T/foto açaí.jpg" 2
h1=$("$FF" -v error -i "foto açaí.jpg" -f rawvideo -pix_fmt rgb24 - | sha /dev/stdin); h2=$("$FF" -v error -i "$(cat "$T/last_out")" -f rawvideo -pix_fmt rgb24 - | sha /dev/stdin)
[ "$h1" = "$h2" ] && echo "  pixels identicos no modo sem perda" || { echo "  >>> FALHOU: pixels mudaram no modo sem perda"; FAILS=$((FAILS+1)); }
run_case "PNG recodificar" "$T/img.png" 1
run_case "PNG sem perda" "$T/img.png" 2
run_case "WEBP recodificar" "$T/img.webp" 1
run_case "WEBP sem perda" "$T/img.webp" 2
run_case "TIFF recodificar" "$T/img.tif" 1
run_case "TIFF sem perda" "$T/img.tif" 2
run_case "GIF (so sem perda)" "$T/img.gif" 0
run_case "BMP recodificar" "$T/img.bmp" 1

# ------------------------------------------------------------ idempotencia e xattr (macOS)
run_case "idempotencia: limpar de novo a saida limpa" "$(cat "$T/last_out")" 1
if command -v xattr >/dev/null 2>&1; then
  cp "foto açaí.jpg" quarentena.jpg; xattr -w com.apple.quarantine "0083;00000000;Safari;" quarentena.jpg 2>/dev/null && \
  xattr -w com.apple.metadata:kMDItemWhereFroms "https://exemplo.com/foto.jpg" quarentena.jpg 2>/dev/null
  run_case "xattr (quarentena/WhereFroms) no modo sem perda" "$T/quarentena.jpg" 2
  if xattr -l "$(cat "$T/last_out")" | grep -q .; then echo "  >>> FALHOU: saida ainda tem xattrs"; xattr -l "$(cat "$T/last_out")"; FAILS=$((FAILS+1)); else echo "  sem xattrs na saida"; fi
fi

echo "=================================================================="
echo "RESULTADO: $PASS passaram, $FAILS falharam"
exit $FAILS
