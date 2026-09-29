# Monta o payload embutido no SemRastro.exe: ffmpeg.exe + arvore do exiftool.
#
#   powershell -ExecutionPolicy Bypass -File pack.ps1
#   powershell -ExecutionPolicy Bypass -File pack.ps1 -DownloadExifTool   # CI / sem instalacao local
#
# Fontes (nesta ordem de preferencia):
#   ffmpeg   -> build "essentials" do gyan.dev (baixado se nao houver cache)
#   exiftool -> -ExifToolDir, ou %LOCALAPPDATA%\Programs\ExifTool (instalador
#               do Oliver Betz), ou o zip oficial de exiftool.org quando
#               -DownloadExifTool e passado ou nao ha instalacao local.
#
# Saida: build\payload.zip  (consumido pelo build.ps1)

param(
    [string]$ExifToolDir = "",
    [switch]$DownloadExifTool,
    [string]$Out = ""
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ProgressPreference = "SilentlyContinue"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root  = Split-Path -Parent $MyInvocation.MyCommand.Path
$build = Join-Path $root "build"
$cache = Join-Path $build "cache"
$stage = Join-Path $build "stage"
$zip   = if ($Out) { $Out } else { Join-Path $build "payload.zip" }

New-Item -ItemType Directory -Force -Path $cache | Out-Null
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# ------------------------------------------------------------
# ffmpeg
# ------------------------------------------------------------
$ffZip = Join-Path $cache "ffmpeg-release-essentials.zip"
if (-not (Test-Path $ffZip)) {
    Write-Host "Baixando ffmpeg (essentials)..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip" `
        -OutFile $ffZip -UseBasicParsing
}

Write-Host "Extraindo ffmpeg.exe..." -ForegroundColor Cyan
$z = [System.IO.Compression.ZipFile]::OpenRead($ffZip)
try {
    $entry = $z.Entries | Where-Object { $_.Name -eq "ffmpeg.exe" } | Select-Object -First 1
    if (-not $entry) { throw "ffmpeg.exe nao encontrado dentro de $ffZip" }
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile(
        $entry, (Join-Path $stage "ffmpeg.exe"), $true)

    # Licenca do build (exigencia da GPL)
    $lic = $z.Entries | Where-Object { $_.Name -eq "LICENSE" -or $_.Name -eq "LICENSE.txt" } | Select-Object -First 1
    if ($lic) {
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile(
            $lic, (Join-Path $stage "ffmpeg-LICENSE.txt"), $true)
    }
}
finally { $z.Dispose() }

# ------------------------------------------------------------
# exiftool
# ------------------------------------------------------------
$etDst = Join-Path $stage "exiftool"
New-Item -ItemType Directory -Force -Path $etDst | Out-Null

if (-not $ExifToolDir) { $ExifToolDir = Join-Path $env:LOCALAPPDATA "Programs\ExifTool" }
$haveLocal = Test-Path (Join-Path $ExifToolDir "ExifTool.exe")

if ($haveLocal -and -not $DownloadExifTool) {
    Write-Host "Copiando exiftool de $ExifToolDir..." -ForegroundColor Cyan
    # unins000.* e o desinstalador do pacote - nao deve viajar junto
    Copy-Item "$ExifToolDir\*" -Destination $etDst -Recurse -Force -Exclude "unins000.*"
    Get-ChildItem $etDst -Recurse -Filter "unins000.*" -Force |
        Remove-Item -Force -ErrorAction SilentlyContinue
}
else {
    # Zip oficial (hospedado no SourceForge): exiftool-<ver>_64.zip contem "exiftool(-k).exe" + exiftool_files\
    # (o "(-k)" no nome faz o exe pausar no fim; renomear tira isso).
    $ver = (Invoke-WebRequest -Uri "https://exiftool.org/ver.txt" -UseBasicParsing).Content.Trim()
    $etZip = Join-Path $cache "exiftool-${ver}_64.zip"
    if (-not (Test-Path $etZip)) {
        Write-Host "Baixando exiftool $ver (zip oficial)..." -ForegroundColor Cyan
        & curl.exe -L -sS -o $etZip "https://sourceforge.net/projects/exiftool/files/exiftool-${ver}_64.zip/download"
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $etZip)) { throw "download do exiftool falhou" }
    }
    Write-Host "Extraindo exiftool..." -ForegroundColor Cyan
    $tmp = Join-Path $build "exiftool-tmp"
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    [System.IO.Compression.ZipFile]::ExtractToDirectory($etZip, $tmp)
    $exe = Get-ChildItem $tmp -Recurse -Filter "exiftool*.exe" | Select-Object -First 1
    if (-not $exe) { throw "exiftool(-k).exe nao encontrado em $etZip" }
    $src = $exe.Directory.FullName
    Copy-Item "$src\*" -Destination $etDst -Recurse -Force
    Get-ChildItem $etDst -Filter "exiftool*.exe" | Rename-Item -NewName "exiftool.exe" -Force
    Remove-Item $tmp -Recurse -Force
}

if (-not (Test-Path (Join-Path $etDst "exiftool.exe")) -and -not (Test-Path (Join-Path $etDst "ExifTool.exe"))) {
    throw "exiftool.exe nao ficou disponivel em $etDst"
}
# O app chama "exiftool.exe"; Windows nao diferencia maiusculas.

# ------------------------------------------------------------
# Empacota
# ------------------------------------------------------------
Write-Host "Compactando payload..." -ForegroundColor Cyan
if (Test-Path $zip) { Remove-Item $zip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Remove-Item $stage -Recurse -Force

Write-Host ""
Write-Host "OK: $zip" -ForegroundColor Green
Write-Host ("payload.zip: {0:N1} MB" -f ((Get-Item $zip).Length / 1MB))
