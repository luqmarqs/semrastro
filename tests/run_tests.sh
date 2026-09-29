#!/bin/bash
# Testes de remocao de metadados: planta valores conhecidos, roda o codigo real
# do app (classe Cleaner, via Harness.exe) e procura os valores na saida - pelo
# exiftool e por busca binaria crua nos bytes.
#
#   bash tests/run_tests.sh          (Git Bash; precisa de python e do runtime
#                                     extraido - o app aberto 1 vez - ou de
#                                     build/payload.zip, que e extraido aqui)
#
# Compila o Harness.exe sozinho a partir de tests/Harness.cs + src/*.cs.
set -u
SP="$(cd "$(dirname "$0")" && pwd)"
RT="$LOCALAPPDATA/SemRastro/runtime"
FF="$RT/ffmpeg.exe"
ET="$RT/exiftool/exiftool.exe"
H="$SP/Harness.exe"
T="$SP/t"

CSC=/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe

# Sem o runtime extraido (CI, maquina nova): extrai build/payload.zip no mesmo
# lugar que o app usaria, para os testes rodarem com os binarios embutidos.
if [ ! -x "$FF" ] && [ -f "$SP/../build/payload.zip" ]; then
  echo "### Extraindo build/payload.zip para $RT"
  mkdir -p "$RT"
  powershell -NoProfile -Command "Expand-Archive -Force -Path '$(cygpath -w "$SP/../build/payload.zip")' -DestinationPath '$(cygpath -w "$RT")'"
fi
[ -x "$FF" ] || { echo "ffmpeg nao encontrado em $RT (rode o app uma vez ou gere build/payload.zip)"; exit 1; }

if [ ! -x "$H" ] || [ "$SP/../src/SemRastro.cs" -nt "$H" ] || [ "$SP/Harness.cs" -nt "$H" ]; then
  echo "### Compilando Harness.exe"
  MSYS_NO_PATHCONV=1 "$CSC" /nologo /target:exe /platform:anycpu /codepage:65001 \
    /main:SemRastro.Harness /out:"$(cygpath -w "$H")" \
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll \
    /reference:System.Windows.Forms.dll /reference:System.IO.Compression.dll \
    "$(cygpath -w "$SP/Harness.cs")" "$(cygpath -w "$SP/../src/SemRastro.cs")" \
    2>&1 | grep -E 'error' && exit 1
fi

rm -rf "$T"; mkdir -p "$T"; cd "$T"

PLANTED=(TITULO_SECRETO ARTISTA_SECRETO "Rua Augusta" STREAM_VIDEO_TITULO STREAM_AUDIO_TITULO
  HANDLER_SECRETO SOFTWARE_SECRETO COPYRIGHT_SECRETO iPhone Apple 23.5613 46.6565
  CAPITULO_SECRETO LEGENDA_SECRETA LIXO_FINAL_SECRETO "2024:05:06" LENTE_SECRETA
  DESCRICAO_SECRETA COMENTARIO_SECRETO BYLINE_SECRETO CIDADE_SECRETA PALAVRA_SECRETA
  CRIADOR_SECRETO DIREITOS_SECRETOS ASSUNTO_SECRETO COMPUTADOR_SECRETO DONO_SECRETO
  SERIAL123456 THUMB_SECRETA DEPOIS_DO_EOI_SECRETO CHUNK_PRIVADO_SECRETO
  ICC_SECRETO Lavf Lavc x264 "por")

SENS='GPS|Location|Latitude|Longitude|Make|Model|Device|Serial|Camera|CreateDate|CreationDate|CreationTime|ModifyDate|Software|Encoder|Author|Artist|Owner|Title|Comment|Description|Copyright|Lens|Creator|By-line|Credit|Keywords|Subject|City|Country|Rights|HostComputer|UserComment|ImageDescription|Thumbnail|Language'

FAILS=0

count_strings() { # file -> "n/total: list"
  local f="$1" n=0 hits=""
  for s in "${PLANTED[@]}"; do
    if LC_ALL=C grep -aqF -- "$s" "$f"; then n=$((n+1)); hits="$hits $s"; fi
  done
  echo "$n/${#PLANTED[@]}:$hits"
}

w() { cygpath -w "$1"; }

run_case() { # name input idx [allowed-surviving-string]
  local name="$1" in="$2" idx="$3" allow="${4:-}"
  echo "=================================================================="
  echo "CASO: $name   (entrada=$(basename "$in"), modo idx=$idx)"
  echo "  entrada  strings plantadas: $(count_strings "$in")"
  local out
  out=$("$H" "$(w "$in")" "$idx" 2>/dev/null | tr -d '\r')
  if [ "$out" = "FAIL" ] || [ -z "$out" ]; then echo "  >>> FALHOU: nenhuma saida produzida"; FAILS=$((FAILS+1)); return; fi
  local outu; outu=$(cygpath -u "$out")
  echo "  saida: $(basename "$outu")  ($(stat -c %s "$outu") bytes, mtime $(stat -c %y "$outu" | cut -c1-19))"
  local res; res=$(count_strings "$outu")
  echo "  saida    strings plantadas: $res"
  local hits="${res#*:}"; hits="${hits# }"
  if [ -n "$hits" ] && [ "$hits" != "$allow" ]; then echo "  >>> FALHOU: sobrou '$hits'"; FAILS=$((FAILS+1)); fi
  echo "  exiftool campos sensiveis na saida:"
  "$ET" -G1 -a -s "$(w "$outu")" | grep -E "$SENS" | sed 's/^/     /'
  echo "  --- todos os campos da saida:"
  "$ET" -G1 -a -s "$(w "$outu")" | sed 's/^/     /'
  echo "$outu" > "$T/last_out"
}

# ------------------------------------------------------------------ VIDEO
echo "### Gerando video de teste"
cat > chap.txt <<'EOF'
;FFMETADATA1
[CHAPTER]
TIMEBASE=1/1000
START=0
END=1000
title=CAPITULO_SECRETO 1
[CHAPTER]
TIMEBASE=1/1000
START=1000
END=2000
title=CAPITULO_SECRETO 2
EOF
printf '1\n00:00:00,000 --> 00:00:01,000\nLEGENDA_SECRETA\n' > sub.srt
"$FF" -hide_banner -loglevel error -y -f lavfi -i testsrc=size=320x240:rate=25:duration=2 \
  -f lavfi -i sine=frequency=440:duration=2 -i chap.txt -i sub.srt \
  -map 0:v -map 1:a -map 3:s -map_metadata 2 -c:v libx264 -pix_fmt yuv420p -c:a aac -c:s mov_text \
  -metadata title=TITULO_SECRETO -metadata artist=ARTISTA_SECRETO \
  -metadata comment="Rua Augusta 1500 Sao Paulo" -metadata copyright=COPYRIGHT_SECRETO \
  -metadata:s:v:0 title=STREAM_VIDEO_TITULO -metadata:s:v:0 language=por -metadata:s:v:0 handler_name=HANDLER_SECRETO \
  -metadata:s:a:0 title=STREAM_AUDIO_TITULO -metadata:s:a:0 language=por \
  video_full.mp4
"$ET" -q -overwrite_original -Keys:GPSCoordinates="-23.5613, -46.6565" -Keys:Make=Apple \
  -Keys:Model="iPhone 15 Pro Max" -Keys:Software=SOFTWARE_SECRETO \
  -Keys:CreationDate="2024:05:06 10:11:12-03:00" -Keys:Description=DESCRICAO_SECRETA video_full.mp4
printf 'LIXO_FINAL_SECRETO' >> video_full.mp4
# "Lavc" e a versao do encoder AAC dentro do bitstream: nao identifica ninguem
run_case "video completo (CRF18)" "$T/video_full.mp4" 1 "Lavc"

echo "### Video com dimensoes impares (321x241)"
"$FF" -hide_banner -loglevel error -y -f lavfi -i testsrc=size=321x241:rate=25:duration=1 -c:v libx264rgb video_odd.mp4
run_case "video dimensoes impares" "$T/video_odd.mp4" 1

echo "### Video com rotacao (display matrix 90)"
"$FF" -hide_banner -loglevel error -y -f lavfi -i testsrc=size=320x240:rate=25:duration=1 -c:v libx264 -pix_fmt yuv420p video_even.mp4
"$FF" -hide_banner -loglevel error -y -display_rotation 90 -i video_even.mp4 -c copy video_rot.mp4
echo "  entrada: $("$ET" -ImageWidth -ImageHeight -Rotation -s -S video_rot.mp4 | tr '\n' ' ')"
run_case "video rotacionado" "$T/video_rot.mp4" 2
rot=$("$ET" -ImageWidth -ImageHeight -Rotation -s -S "$(cat "$T/last_out")" | tr -d '\r' | tr '\n' ' ')
echo "  saida: $rot"
case "$rot" in "240 320 0 ") ;; *) echo "  >>> FALHOU: rotacao nao aplicada fisicamente"; FAILS=$((FAILS+1));; esac

echo "### Video 10-bit"
"$FF" -hide_banner -loglevel error -y -f lavfi -i testsrc=size=320x240:rate=25:duration=1 -c:v libx264 -pix_fmt yuv420p10le video_10bit.mp4
run_case "video 10-bit" "$T/video_10bit.mp4" 2

echo "### Video sem audio, MKV"
"$FF" -hide_banner -loglevel error -y -f lavfi -i testsrc=size=320x240:rate=25:duration=1 -c:v libx264 -pix_fmt yuv420p -metadata title=TITULO_SECRETO video_noaudio.mkv
run_case "mkv sem audio" "$T/video_noaudio.mkv" 2

# ------------------------------------------------------------------ IMAGENS
echo "### Gerando imagens de teste"
"$FF" -hide_banner -loglevel error -y -f lavfi -i testsrc=size=160x120 -frames:v 1 -update 1 thumb.jpg
"$ET" -q -overwrite_original -Comment=THUMB_SECRETA thumb.jpg
"$FF" -hide_banner -loglevel error -y -f lavfi -i testsrc=size=640x480 -frames:v 1 -update 1 base.jpg
"$FF" -hide_banner -loglevel error -y -f lavfi -i testsrc=size=640x480 -frames:v 1 -update 1 base.png
"$FF" -hide_banner -loglevel error -y -f lavfi -i testsrc=size=640x480 -frames:v 1 -update 1 -c:v libwebp base.webp
"$FF" -hide_banner -loglevel error -y -f lavfi -i testsrc=size=640x480 -frames:v 1 -update 1 base.tif
"$FF" -hide_banner -loglevel error -y -f lavfi -i testsrc=size=640x480 -frames:v 1 -update 1 base.bmp
"$FF" -hide_banner -loglevel error -y -f lavfi -i testsrc=size=160x120:rate=5:duration=1 base.gif
"$FF" -hide_banner -loglevel error -y -f lavfi -i testsrc=size=64x64 -frames:v 1 -c:v libaom-av1 -still-picture 1 -f avif base.avif 2>/dev/null

EXIFARGS=(-Make=Apple -Model="iPhone 15 Pro Max" -LensModel=LENTE_SECRETA -Software=SOFTWARE_SECRETO
  -Artist=ARTISTA_SECRETO -Copyright=COPYRIGHT_SECRETO -ImageDescription=DESCRICAO_SECRETA
  -UserComment=COMENTARIO_SECRETO -GPSLatitude=23.5613 -GPSLatitudeRef=S -GPSLongitude=46.6565 -GPSLongitudeRef=W
  -HostComputer=COMPUTADOR_SECRETO -OwnerName=DONO_SECRETO -SerialNumber=SERIAL123456
  -DateTimeOriginal="2024:05:06 10:11:12"
  -XMP:Creator=CRIADOR_SECRETO -XMP:Rights=DIREITOS_SECRETOS -XMP:Subject=ASSUNTO_SECRETO)
IPTCARGS=(-IPTC:By-line=BYLINE_SECRETO -IPTC:City=CIDADE_SECRETA -IPTC:Keywords=PALAVRA_SECRETA)

cp base.jpg img.jpg
"$ET" -q -overwrite_original "${EXIFARGS[@]}" "${IPTCARGS[@]}" "-ThumbnailImage<=thumb.jpg" img.jpg
printf 'DEPOIS_DO_EOI_SECRETO' >> img.jpg

cp base.png img.png
"$ET" -q -overwrite_original "${EXIFARGS[@]}" -PNG:Title=TITULO_SECRETO -PNG:Author=ARTISTA_SECRETO -PNG:Comment=COMENTARIO_SECRETO img.png
python - img.png <<'EOF'
import struct, zlib, sys
p=sys.argv[1]; d=open(p,'rb').read()
i=d.rfind(b'IEND')-4
body=b'CHUNK_PRIVADO_SECRETO'
c=struct.pack('>I',len(body))+b'prVt'+body+struct.pack('>I',zlib.crc32(b'prVt'+body)&0xffffffff)
open(p,'wb').write(d[:i]+c+d[i:])
EOF

cp base.webp img.webp
"$ET" -q -overwrite_original "${EXIFARGS[@]}" img.webp

cp base.tif img.tif
"$ET" -q -overwrite_original "${EXIFARGS[@]}" "${IPTCARGS[@]}" img.tif

cp base.gif img.gif
"$ET" -q -overwrite_original -Comment=COMENTARIO_SECRETO -XMP:Creator=CRIADOR_SECRETO -XMP:Subject=ASSUNTO_SECRETO img.gif

cp base.bmp img.bmp

if [ -f base.avif ]; then
  cp base.avif img.avif
  "$ET" -q -overwrite_original -XMP:Creator=CRIADOR_SECRETO -Make=Apple -Model="iPhone 15 Pro Max" -GPSLatitude=23.5 -GPSLatitudeRef=S img.avif
fi

for f in img.jpg img.png img.webp img.tif img.gif; do
  echo "  $f: $("$ET" -G1 -a -s "$f" | grep -cE "$SENS") campos sensiveis plantados"
done

run_case "JPG recodificar (alta)" "$T/img.jpg" 1
run_case "JPG sem perda" "$T/img.jpg" 2
lossless_out=$(cat "$T/last_out")
run_case "PNG recodificar" "$T/img.png" 1
run_case "PNG sem perda" "$T/img.png" 2
chunks=$(python -c "
import struct,sys;d=open(sys.argv[1],'rb').read();p=8;t=[]
while p+12<=len(d):
    l=struct.unpack('>I',d[p:p+4])[0];t.append(d[p+4:p+8].decode());p+=12+l
print(' '.join(t))" "$(cat "$T/last_out")")
echo "  chunks PNG restantes: $chunks"
[ "$chunks" = "IHDR IDAT IEND" ] || { echo "  >>> FALHOU: chunks inesperados"; FAILS=$((FAILS+1)); }
run_case "WEBP recodificar" "$T/img.webp" 1
run_case "WEBP sem perda" "$T/img.webp" 2
run_case "TIFF recodificar" "$T/img.tif" 1
run_case "TIFF sem perda" "$T/img.tif" 2
run_case "GIF (so sem perda)" "$T/img.gif" 0
run_case "BMP recodificar" "$T/img.bmp" 1
[ -f img.avif ] && run_case "AVIF (so sem perda)" "$T/img.avif" 0

# hash dos pixels no modo sem perda (JPG)
echo "=================================================================="
echo "### Hash dos pixels decodificados (JPG original vs sem perda)"
h1=$("$FF" -hide_banner -loglevel error -i img.jpg -f rawvideo -pix_fmt rgb24 - | md5sum | cut -c1-16)
h2=$("$FF" -hide_banner -loglevel error -i "$lossless_out" -f rawvideo -pix_fmt rgb24 - | md5sum | cut -c1-16)
echo "  img.jpg: $h1"
echo "  $(basename "$lossless_out"): $h2"
[ "$h1" = "$h2" ] || { echo "  >>> FALHOU: pixels mudaram no modo sem perda"; FAILS=$((FAILS+1)); }

echo "=================================================================="
if [ "$FAILS" -eq 0 ]; then echo "RESULTADO: todos os casos passaram"; else echo "RESULTADO: $FAILS falha(s)"; fi
exit $FAILS
