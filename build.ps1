# Compila o SemRastro.exe usando o compilador C# que ja vem no Windows.
# Nao precisa instalar nada (Visual Studio, .NET SDK, Python...).
#
#   powershell -ExecutionPolicy Bypass -File build.ps1

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root "src\SemRastro.cs"
$out  = Join-Path $root "SemRastro.exe"

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path $csc)) {
    throw "csc.exe do .NET Framework 4 nao encontrado."
}

# O payload (ffmpeg + exiftool) e opcional: sem ele o .exe sai com ~30 KB e
# procura os binarios instalados no sistema. Gere com: pack.ps1
$payload = Join-Path $root "build\payload.zip"
$resArg = @()
if (Test-Path $payload) {
    Write-Host ("Embutindo payload ({0:N1} MB)..." -f ((Get-Item $payload).Length / 1MB)) -ForegroundColor Cyan
    $resArg = @("/resource:$payload,payload")
} else {
    Write-Host "Sem build\payload.zip - gerando .exe sem dependencias embutidas." -ForegroundColor Yellow
}

# Icone do .exe e manifesto (DPI awareness + controles v6). Opcionais.
$icon = Join-Path $root "assets/app.ico"
$manifest = Join-Path $root "assets/app.manifest"
$extra = @()
if (Test-Path $icon) { $extra += "/win32icon:$icon" }
if (Test-Path $manifest) { $extra += "/win32manifest:$manifest" }

Write-Host "Compilando..." -ForegroundColor Cyan

& $csc `
    /nologo `
    /target:winexe `
    /platform:anycpu `
    /optimize+ `
    /codepage:65001 `
    /out:"$out" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.IO.Compression.dll `
    @resArg `
    @extra `
    "$src"

if ($LASTEXITCODE -ne 0) { throw "Falha na compilacao." }

Write-Host ""
Write-Host "OK: $out" -ForegroundColor Green
Write-Host ("Tamanho: {0:N0} KB" -f ((Get-Item $out).Length / 1KB))
