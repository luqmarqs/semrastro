# Teste de interface: abre o app com arquivos, localiza cada controle pelo
# nome na arvore de UI Automation e o aciona pelo teclado (WM_KEYDOWN/UP
# direto no HWND do controle: Espaco no botao, Delete na linha). E o mesmo
# caminho de quem usa Tab + teclado. Nao move o mouse nem depende de foco.
#   powershell -ExecutionPolicy Bypass -File tests\ui_test.ps1 -OutDir C:\screenshots `
#       -Files "`"C:\v\ferias.mp4`" `"C:\v\foto.jpg`" `"C:\v\tela.png`" `"C:\v\reacao.gif`""
# O primeiro arquivo precisa ser um video de alguns segundos (da tempo de
# cancelar no meio) e o ultimo precisa se chamar reacao.gif (e o que o Delete
# remove). Sai com erro se algum passo nao encontrar o controle esperado.
param([string]$Files, [string]$OutDir, [string]$Exe = "$PSScriptRoot\..\SemRastro.exe")
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public class W {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static void Key(IntPtr h, int vk) {
    PostMessage(h, 0x0100, (IntPtr)vk, (IntPtr)1);            // WM_KEYDOWN
    PostMessage(h, 0x0101, (IntPtr)vk, (IntPtr)(1 | (1 << 30) | (1 << 31))); // WM_KEYUP
  }
}
"@
[W]::SetProcessDPIAware() | Out-Null
$env:SEMRASTRO_THEME = "dark"

function Shot($h, $name) {
  $r = New-Object W+RECT; [W]::GetWindowRect($h, [ref]$r) | Out-Null
  $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
  $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
  [W]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc)
  $bmp.Save((Join-Path $OutDir "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
}
function Elements($root) { @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) }
function Names($root) { (Elements $root | Where-Object { $_.Current.Name -and $_.Current.Name.Length -lt 40 } | ForEach-Object { "'" + $_.Current.Name + "'" + $(if ($_.Current.IsEnabled) { "" } else { "(off)" }) }) -join " " }
function Key-Named($root, [string]$pattern, [int]$vk) {
  $el = Elements $root | Where-Object { $_.Current.Name -match $pattern } | Select-Object -First 1
  if ($null -eq $el) { throw "controle /$pattern/ nao encontrado entre: $(Names $root)" }
  if (-not $el.Current.IsEnabled) { throw "controle '$($el.Current.Name)' esta desabilitado" }
  [W]::Key([IntPtr]$el.Current.NativeWindowHandle, $vk)
  "tecla 0x{0:X} em: {1}" -f $vk, $el.Current.Name
}
$SPACE = 0x20; $DEL = 0x2E

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe; $psi.Arguments = $Files; $psi.UseShellExecute = $false
$p = [System.Diagnostics.Process]::Start($psi)
try {
  Start-Sleep -Seconds 4; $p.Refresh(); $h = $p.MainWindowHandle
  $root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
  "fila:          " + (Names $root)

  # 1. remove um arquivo da fila (Delete na linha)
  Key-Named $root '^reacao\.gif$' $DEL
  Start-Sleep -Milliseconds 800
  "apos remover:  " + (Names $root)
  Shot $h "uia-0-removido"

  # 2. inicia o lote e cancela no meio do video (primeiro da fila)
  Key-Named $root '^Limpar \d+ arquivos$' $SPACE
  Start-Sleep -Seconds 3
  "rodando:       " + (Names $root)
  Shot $h "uia-1-rodando"
  Key-Named $root '^Cancelar$' $SPACE
  Start-Sleep -Seconds 2
  "apos cancelar: " + (Names $root)
  Shot $h "uia-2-cancelado"

  # 3. roda de novo ate o fim (so os pendentes)
  Key-Named $root '^Limpar ([0-9]+ arquivos|metadados)$' $SPACE
  Start-Sleep -Seconds 40
  "no fim:        " + (Names $root)
  Shot $h "uia-3-pronto"

  # 4. abre detalhes e limpa a lista
  Key-Named $root '^Detalhes$' $SPACE
  Start-Sleep -Milliseconds 800
  Shot $h "uia-4-detalhes"
  Key-Named $root '^Limpar lista$' $SPACE
  Start-Sleep -Milliseconds 800
  "apos limpar:   " + (Names $root)
  Shot $h "uia-5-vazio"
  "fim"
}
finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}
