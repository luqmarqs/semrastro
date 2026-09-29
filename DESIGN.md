# Design da interface

Referência para a versão Windows (implementada em [src/SemRastro.cs](src/SemRastro.cs))
e para uma futura versão macOS. A ideia é que as duas pareçam nativas na plataforma
onde rodam, e ao mesmo tempo sejam o mesmo app: mesma estrutura de tela, mesmos
textos, mesmos estados.

## Princípios

1. **Uma tela, uma ação.** O app faz uma coisa. Não há menu, não há abas, não há
   configurações escondidas. Tudo cabe numa janela de 820×640.
2. **O arquivo é o protagonista.** A área de soltar ocupa a tela inteira quando não
   há nada, e encolhe para uma faixa quando a fila existe. A fila mostra o que vai
   acontecer e o que aconteceu com cada arquivo.
3. **Ruído só quando pedido.** O log técnico (comandos, auditoria do exiftool) fica
   atrás de "Detalhes", fechado por padrão. Abre sozinho só quando algo falha.
4. **Estado sempre visível.** Cada arquivo tem um status legível (Na fila, 43%,
   Limpo, Erro, Cancelado). O botão principal diz quantos arquivos vai limpar.
5. **Segue o sistema.** Tema claro/escuro e cor de destaque vêm do sistema
   operacional, não de uma preferência do app.

Referências consultadas: as diretrizes de design do Windows 11 (tipografia Segoe UI
Variable, espaçamento em múltiplos de 4/8 px, raio de 4 px em controles e 8 px em
cartões, cor de destaque do sistema, modo escuro), as Human Interface Guidelines da
Apple (arrastar e soltar, janela única para apps de propósito único) e apps do
mesmo gênero: ExifCleaner, Metadata Cleaner (GNOME) e ImageOptim, que convergem no
mesmo padrão de "área de soltar + lista com status por arquivo + um botão".

## Estrutura da tela (as duas plataformas)

```
┌──────────────────────────────────────────────────────────────┐
│ Título                                                        │
│ Subtítulo: o que o app faz e a promessa de privacidade        │
├──────────────────────────────────────────────────────────────┤
│ [ Área de soltar ]  expandida (vazio)  /  compacta (com fila) │
├──────────────────────────────────────────────────────────────┤
│ Fila                                                          │
│   ícone  nome do arquivo                          status      │
│          tipo · tamanho · pasta  (ou "salvo como …")          │
├──────────────────────────────────────────────────────────────┤
│ Vídeo  [Máxima|Alta|Média]   Imagem [Recod. máx|Recod.|Sem perda] │
├──────────────────────────────────────────────────────────────┤
│ ▬▬▬▬ progresso · status      [Detalhes] [Abrir pasta] [Limpar N] │
│ (log técnico, recolhido)                                       │
│ rodapé: origem do ffmpeg/exiftool · onde a saída é gravada     │
└──────────────────────────────────────────────────────────────┘
```

Estados da fila: **Na fila** → **N%** (linha fina de progresso sob a linha) →
**Limpo** (verde, ícone de check) / **Erro** (vermelho, ícone de alerta) /
**Cancelado** (cinza). Os controles de qualidade só aparecem para os tipos presentes
na fila (só vídeo, só imagem, ou os dois).

Botão principal: "Limpar metadados" com 1 arquivo, "Limpar N arquivos" com vários,
"Cancelar" (vermelho) durante a execução. Atalhos: Ctrl+O abre o seletor, Enter
inicia, Esc cancela.

## Windows (implementado)

- **Tipografia**: Segoe UI Variable Display Semibold 15 pt no título; Segoe UI
  Variable Text 10 pt no corpo; Segoe UI Variable Small 8,75 pt em legendas;
  Cascadia Mono 9 pt no log. Ícones de Segoe Fluent Icons (fallback: Segoe MDL2).
- **Cores** (Fluent 2). Escuro: fundo #202020, cartão #2B2B2B, borda #3A3A3A,
  texto #FFFFFF / #C8C8C8 / #8C8C8C. Claro: fundo #F3F3F3, cartão #FFFFFF, borda
  #E5E5E5, texto #1B1B1B / #5D5D5D / #8A8A8A. Semânticas: verde #6CCB5F / #0F7B0F,
  amarelo #FCE100 / #9D5D00, vermelho #FF99A4 / #C42B1C.
- **Cor de destaque**: lida de `HKCU\...\Explorer\Accent\AccentColorMenu`. No tema
  escuro o botão principal usa um tom clareado do accent com texto preto, como o
  Windows 11 faz; no claro, o accent puro com texto branco.
- **Barra de título** na cor da janela via `DwmSetWindowAttribute`
  (`DWMWA_CAPTION_COLOR`, `DWMWA_TEXT_COLOR`, `USE_IMMERSIVE_DARK_MODE`).
- **Controles**: desenhados à mão (WinForms não tem tema escuro nativo): botões com
  raio 4 px, segmented control para as qualidades, área de soltar com borda
  tracejada que vira sólida na cor de destaque ao arrastar por cima.
- **DPI**: manifesto declara DPI awareness de sistema; medidas em px de 96 dpi
  escaladas por `GetDpiForSystem`. Fontes em pontos escalam sozinhas.
- **Ícone**: escudo branco com check em quadrado azul arredondado
  ([assets/app.ico](assets/app.ico), 16 a 256 px).

## macOS (a implementar)

Mesma estrutura, mas com as convenções do Mac. O que muda:

- **Tecnologia**: SwiftUI (nativo) ou Avalonia (reaproveita a classe `Cleaner` em
  C#). Em qualquer caso, a lógica de limpeza é a mesma; só a camada de UI muda.
- **Janela**: título na barra unificada (`.titlebar` com `toolbarStyle(.unified)`),
  sem o título repetido dentro da janela. O subtítulo vira a primeira linha do
  conteúdo. Fundo com material padrão da janela (`.windowBackground`), não cor
  chapada. No macOS 26 (Tahoe), os controles de vidro ("Liquid Glass") vêm de
  graça pelos componentes padrão; não recriar à mão.
- **Tipografia**: SF Pro pelos estilos de texto do sistema (`.title2` para o título,
  `.body` no corpo, `.caption` em legendas, `SF Mono` no log). Nunca tamanhos fixos.
- **Cores**: cores semânticas do sistema (`.primary`, `.secondary`,
  `Color.accentColor`, `.green`, `.red`) para que tema claro/escuro, cor de destaque
  do usuário e alto contraste funcionem sem código.
- **Controles**: botão principal como `.borderedProminent`; qualidades como
  `Picker` com `.segmented`; "Detalhes" como `DisclosureGroup`; a fila como `List`
  com `.inset` e linhas com `Label` + status à direita. Área de soltar com
  `.dropDestination(for: URL.self)` e borda tracejada só no estado vazio.
- **Arrastar e soltar**: também aceitar arquivos soltos no ícone do Dock e o
  serviço "Abrir com". Aceitar pastas.
- **Diálogos**: erros como `.alert`, não como texto vermelho solto. Confirmação de
  sair durante a execução como sheet.
- **Pós-processamento específico do Mac**: além do que o pipeline já faz, remover
  os atributos estendidos da saída (`xattr -c`), porque o macOS grava a URL de
  origem em `com.apple.metadata:kMDItemWhereFroms` e a quarentena em
  `com.apple.quarantine`. É o equivalente do `Zone.Identifier` do NTFS.
- **Binários**: ffmpeg estático universal (arm64 + x86_64) dentro do bundle; o
  exiftool é Perl e roda no Perl que o macOS traz. Assinar o bundle com Developer
  ID e notarizar, senão o Gatekeeper bloqueia.
- **Ícone**: mesmo escudo, mas no formato "squircle" do macOS, gerado pelo Icon
  Composer / `iconutil` a partir do PNG de 1024 px.

## O que não fazer

- Não adicionar preferências. Se uma opção não cabe na tela, ela não precisa existir.
- Não mostrar o log por padrão. Quem quer ver, abre.
- Não usar cores próprias para estados que o sistema já define (destaque, erro).
- Não usar ícones de emoji: no Windows são os glifos de Segoe Fluent Icons, no Mac
  são SF Symbols.
