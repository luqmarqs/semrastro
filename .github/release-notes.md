## Qual arquivo baixar

| Sistema | Arquivo |
|---|---|
| Windows 10/11 (64 bits) | `SemRastro-<versão>-windows-x64.exe` — portátil, dois cliques, nada a instalar |
| macOS com Apple Silicon (M1, M2, M3, M4…) | `SemRastro-<versão>-macos-arm64.dmg` |
| macOS com Intel | `SemRastro-<versão>-macos-x64.dmg` |
| Linux x86_64 | `SemRastro-<versão>-linux-x86_64.AppImage` |
| Linux ARM64 (Raspberry Pi 4/5 64 bits, etc.) | `SemRastro-<versão>-linux-aarch64.AppImage` |

Os links **"Source code (zip / tar.gz)"** são o código-fonte, não o programa.

Todos os pacotes trazem FFmpeg e ExifTool dentro. Nenhum precisa de instalação separada
(no macOS e no Linux, o ExifTool usa o `perl` que já vem no sistema).

## Verificação

`SHA256SUMS.txt` traz o hash de cada arquivo. O hash prova que o download chegou íntegro;
não é assinatura de editor.

- **Windows**: o executável não é assinado; o SmartScreen avisa na primeira execução
  ("Mais informações" → "Executar assim mesmo").
- **macOS**: sem Developer ID e sem notarização. Na primeira abertura: Ajustes do Sistema →
  Privacidade e Segurança → "Abrir Mesmo Assim". Veja o README.
- **Linux**: dê permissão de execução ao AppImage (`chmod +x`) e execute. Sem FUSE,
  use `--appimage-extract-and-run`.

Contém FFmpeg (GPL) e ExifTool (Artistic/GPL) como programas separados; veja THIRD_PARTY_NOTICES.md.
