param(
    [Parameter(Mandatory=$true)]
    [string]$InputFile
)

$ErrorActionPreference = "Stop"

# Resolve caminho absoluto
$InputFile = (Resolve-Path $InputFile).Path
$dir = Split-Path $InputFile

# Gera nome aleatório para não carregar informação do filename original
$randomName = [guid]::NewGuid().ToString("N") + ".mp4"
$output = Join-Path $dir $randomName

Write-Host ""
Write-Host "Arquivo original:"
Write-Host $InputFile
Write-Host ""
Write-Host "Arquivo sanitizado:"
Write-Host $output
Write-Host ""

# ------------------------------------------------------------
# 1. Reencodificação completa
# ------------------------------------------------------------

ffmpeg `
    -hide_banner `
    -y `
    -i "$InputFile" `
    -map 0:v:0 `
    -map 0:a? `
    -map_metadata -1 `
    -map_chapters -1 `
    -sn `
    -dn `
    -c:v libx264 `
    -preset medium `
    -crf 18 `
    -pix_fmt yuv420p `
    -c:a aac `
    -b:a 192k `
    -metadata title="" `
    -metadata artist="" `
    -metadata author="" `
    -metadata comment="" `
    -metadata copyright="" `
    -metadata description="" `
    -metadata creation_time="" `
    -metadata:s:v:0 handler_name="" `
    -metadata:s:a:0 handler_name="" `
    -metadata:s:v:0 encoder="" `
    -metadata:s:a:0 encoder="" `
    -movflags +faststart `
    "$output"

if ($LASTEXITCODE -ne 0) {
    throw "FFmpeg falhou."
}

# ------------------------------------------------------------
# 2. ExifTool: remove metadata que ainda possa ser removida
# ------------------------------------------------------------

exiftool `
    -all= `
    -overwrite_original `
    "$output"

if ($LASTEXITCODE -ne 0) {
    Write-Warning "ExifTool retornou erro. Confira manualmente."
}

# ------------------------------------------------------------
# 3. Neutraliza timestamps do arquivo no Windows
#
# Isso NÃO é metadata interna do vídeo.
# São atributos do arquivo no filesystem NTFS.
# ------------------------------------------------------------

$neutralDate = Get-Date "2000-01-01 00:00:00"

$item = Get-Item "$output"
$item.CreationTime   = $neutralDate
$item.LastWriteTime  = $neutralDate
$item.LastAccessTime = $neutralDate

# ------------------------------------------------------------
# 4. Auditoria
# ------------------------------------------------------------

Write-Host ""
Write-Host "=============================================="
Write-Host " AUDITORIA DE METADADOS"
Write-Host "=============================================="
Write-Host ""

$metadata = exiftool -G1 -a -s "$output"

$metadata | Out-Host

Write-Host ""
Write-Host "=============================================="
Write-Host " CAMPOS POTENCIALMENTE SENSÍVEIS"
Write-Host "=============================================="
Write-Host ""

$sensitivePatterns = @(
    "GPS",
    "Location",
    "Latitude",
    "Longitude",
    "Make",
    "Model",
    "Device",
    "Serial",
    "Camera",
    "CreateDate",
    "CreationDate",
    "CreationTime",
    "ModifyDate",
    "MediaCreateDate",
    "TrackCreateDate",
    "Software",
    "Encoder",
    "Author",
    "Artist",
    "Owner",
    "Title",
    "Comment",
    "Description",
    "Copyright"
)

$found = @()

foreach ($pattern in $sensitivePatterns) {
    $matches = $metadata | Select-String -Pattern $pattern -CaseSensitive:$false

    if ($matches) {
        $found += $matches
    }
}

if ($found.Count -eq 0) {
    Write-Host "Nenhum campo sensível óbvio encontrado."
}
else {
    Write-Host "ATENÇÃO: revise os seguintes campos:"
    Write-Host ""

    $found |
        ForEach-Object { $_.Line } |
        Sort-Object -Unique |
        Out-Host
}

Write-Host ""
Write-Host "=============================================="
Write-Host " RESULTADO"
Write-Host "=============================================="
Write-Host ""
Write-Host $output