# Componentes de terceiros

O SemRastro chama dois programas de terceiros, **FFmpeg** e **ExifTool**, como
processos separados. Eles viajam dentro de cada pacote, mas **não** são compilados
junto com o código deste projeto: o `.exe`, o `.app` e o `.AppImage` são uma
agregação, e cada componente mantém a própria licença.

| Pacote | Onde ficam |
|---|---|
| Windows `SemRastro.exe` | dentro do `.exe`; extraídos para `%LOCALAPPDATA%\SemRastro\runtime` na primeira execução |
| macOS `SemRastro.app` | `Contents/Resources/bin/ffmpeg` e `Contents/Resources/exiftool/` |
| Linux `.AppImage` | `usr/bin/ffmpeg` e `usr/share/semrastro/exiftool/` |

As versões, URLs e hashes SHA-256 dos binários usados no macOS e no Linux estão em
[packaging/tools.lock](packaging/tools.lock). No Windows, o `pack.ps1` documenta as fontes.

## FFmpeg

- Site: https://ffmpeg.org — licença **GPL** nos builds usados (incluem libx264).
- Windows: build "essentials" de https://www.gyan.dev/ffmpeg/builds/ (fonte publicado por versão no mesmo site).
- macOS (arm64 e x86_64): builds de https://ffmpeg.martin-riedl.de (versão 9.0.2).
- Linux (x86_64 e aarch64): builds estáticos de https://johnvansickle.com/ffmpeg/ (versão 7.0.2, GPLv3).
- O texto da licença acompanha cada pacote como `ffmpeg-LICENSE.txt`. Quem redistribuir
  deve manter esse aviso e a disponibilidade do código-fonte correspondente, como a GPL
  exige; a forma segura é anexar o tarball de fonte do FFmpeg à mesma release.

## ExifTool

- Site: https://exiftool.org — autor Phil Harvey — licença igual à do Perl (**Artistic** ou **GPL**).
- Windows: pacote com Perl embutido (zip oficial ou instalador de Oliver Betz); licenças em `exiftool\LICENSE` e `Licenses_Strawberry_Perl.zip`.
- macOS e Linux: distribuição Perl pura `Image-ExifTool-13.59`; o `README` original vai como `exiftool/LICENSE`. Usa o interpretador `perl` do sistema (`/usr/bin/perl`), que não é redistribuído.

## Runtime e bibliotecas (macOS e Linux)

| Componente | Licença | Uso |
|---|---|---|
| .NET Runtime (Microsoft) | MIT | embutido no `.app` e no `.AppImage` (self-contained) |
| Avalonia UI, Avalonia.Themes.Fluent | MIT | interface |
| Avalonia.Fonts.Inter (fonte Inter, Rasmus Andersson) | SIL Open Font License 1.1 | fonte da interface |
| SkiaSharp / HarfBuzzSharp (dependências do Avalonia) | MIT | renderização |
| AppImage runtime (type2-runtime) | MIT | somente no `.AppImage` |

A versão Windows usa apenas o .NET Framework e as fontes já instaladas no sistema
(Segoe UI Variable, Segoe Fluent Icons, Cascadia Mono); nenhuma fonte é distribuída.

---

O código próprio deste projeto (`src/`, `tests/`, `packaging/`, scripts) está sob a licença
indicada em [LICENSE](LICENSE) (MIT).
