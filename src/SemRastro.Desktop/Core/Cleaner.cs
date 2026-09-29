using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// Pipeline de limpeza, sem nenhuma dependencia de interface, para o app
// multiplataforma (macOS/Linux). E uma COPIA da classe Cleaner de src/SemRastro.cs
// (a versao Windows, que nao e alterada), com tres adaptacoes: exiftool via perl,
// datas do arquivo sem NTFS e remocao de atributos estendidos (xattr).
// Ao corrigir o pipeline num arquivo, replique no outro.

namespace SemRastro
{
    // ------------------------------------------------------------
    // Tipos de arquivo
    // ------------------------------------------------------------
    public static class Kinds
    {
        public static readonly string[] VideoExt = {
            ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v",
            ".wmv", ".flv", ".ts", ".mpg", ".mpeg", ".3gp", ".m2ts"
        };

        public static readonly string[] ImageExt = {
            ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tif", ".tiff",
            ".heic", ".heif", ".avif", ".gif"
        };

        // Formatos que o ffmpeg nao reencoda bem (ou cuja recodificacao
        // destruiria o arquivo, como a animacao de um GIF): so limpeza sem perda.
        public static readonly string[] LosslessOnlyExt = {
            ".heic", ".heif", ".avif", ".gif"
        };

        public static bool IsVideo(string path)
        {
            return Array.IndexOf(VideoExt, Path.GetExtension(path).ToLowerInvariant()) >= 0;
        }

        public static bool IsImage(string path)
        {
            return Array.IndexOf(ImageExt, Path.GetExtension(path).ToLowerInvariant()) >= 0;
        }

        public static bool IsLosslessOnly(string path)
        {
            return Array.IndexOf(LosslessOnlyExt, Path.GetExtension(path).ToLowerInvariant()) >= 0;
        }

        public static bool IsSupported(string path)
        {
            return IsVideo(path) || IsImage(path);
        }
    }

    public enum LogKind { Text, Muted, Accent, Ok, Warn, Err }

    // ------------------------------------------------------------
    // Pipeline de limpeza de UM arquivo. Sem UI: fala por callbacks.
    // ------------------------------------------------------------
    public class Cleaner
    {
        public string FfmpegPath;
        public string ExiftoolPath;
        // No macOS e no Linux o exiftool e um script Perl: roda como "perl exiftool ...".
        // No Windows fica null e o exiftool.exe e chamado direto.
        public string PerlPath;

        public Action<string, LogKind> Log = delegate { };
        public Action<string> Status = delegate { };
        public Action<int> Progress = delegate { };   // 0..100 do arquivo atual

        public volatile bool Cancelled;
        Process current;

        static readonly string[] SensitivePatterns = {
            // comuns
            "GPS", "Location", "Latitude", "Longitude", "Make", "Model", "Device",
            "Serial", "Camera", "CreateDate", "CreationDate", "CreationTime",
            "ModifyDate", "MediaCreateDate", "TrackCreateDate", "Software",
            "Encoder", "Author", "Artist", "Owner", "Title", "Comment",
            "Description", "Copyright",
            // tipicos de foto (EXIF / IPTC / XMP)
            "Lens", "Creator", "By-line", "Credit", "Keywords", "Subject",
            "City", "Country", "Province", "Sublocation", "Rights",
            "HostComputer", "Firmware", "UserComment", "ImageDescription",
            "Rating", "PersonIn", "RegionName", "Thumbnail"
        };

        public void Cancel()
        {
            Cancelled = true;
            Process p = current;
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(); }
            catch { }
        }

        // videoIdx: 0 = CRF 14 / 1 = CRF 18 / 2 = CRF 23
        // imageIdx: 0 = recodificar maxima / 1 = recodificar alta / 2 = sem perda
        // Retorna o caminho da saida, ou null se falhou / cancelou.
        public string Run(string inputFile, int videoIdx, int imageIdx)
        {
            Cancelled = false;
            string result = null;
            try
            {
                string dir = Path.GetDirectoryName(inputFile);
                bool isImage = Kinds.IsImage(inputFile);
                int idx = isImage ? imageIdx : videoIdx;

                // Imagem sem perda mantem os pixels intactos e so remove metadata.
                bool lossless = isImage && (idx == 2 || Kinds.IsLosslessOnly(inputFile));
                // BMP nao tem campo de metadata e o exiftool nao escreve BMP:
                // recodificar ja e sem perda e e a unica forma de limpar.
                if (lossless && Path.GetExtension(inputFile).ToLowerInvariant() == ".bmp")
                {
                    lossless = false;
                    Log("BMP: recodificacao e sem perda por natureza; usando esse modo.", LogKind.Muted);
                }

                // Nome aleatorio para nao carregar informacao do filename original.
                // Imagem preserva a extensao; video sempre sai como .mp4.
                string ext = isImage ? Path.GetExtension(inputFile).ToLowerInvariant() : ".mp4";
                string outPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ext);

                Log("Arquivo original:", LogKind.Muted);
                Log(inputFile, LogKind.Text);
                Log("", LogKind.Text);
                Log("Arquivo sanitizado:", LogKind.Muted);
                Log(outPath, LogKind.Text);
                Log("", LogKind.Text);

                // 1. Gera o arquivo de saida
                int code;
                if (lossless)
                {
                    Status("Copiando (sem perda)...");
                    Log("[1/4] Copia sem perda - pixels intactos", LogKind.Accent);
                    if (ExiftoolPath == null)
                    {
                        Log("  Sem o exiftool o modo sem perda nao limpa nada.", LogKind.Err);
                        Log("  Instale o exiftool ou use 'Recodificar'.", LogKind.Err);
                        Status("exiftool ausente.");
                        return null;
                    }
                    // File.Copy usaria CopyFile do Win32, que arrasta junto os
                    // alternate data streams do NTFS - inclusive o Zone.Identifier,
                    // que guarda a URL de onde o arquivo foi baixado. Copiar so o
                    // fluxo principal evita isso.
                    CopyDataOnly(inputFile, outPath);

                    // Sem recodificar, so removemos o que sabemos ler. Em PNG a
                    // varredura de chunks e em TIFF a varredura do IFD resolvem;
                    // nos demais, nao ha garantia.
                    if (ext != ".png" && ext != ".tif" && ext != ".tiff")
                    {
                        Log("  aviso: sem recodificar, dados escondidos em blocos", LogKind.Warn);
                        Log("  proprietarios podem sobreviver. Para limpeza total,", LogKind.Warn);
                        Log("  use 'Recodificar' (ou converta para PNG/JPG).", LogKind.Warn);
                    }
                    code = 0;
                }
                else if (isImage)
                {
                    Status("Recodificando imagem...");
                    Log("[1/4] Recodificacao da imagem (ffmpeg)", LogKind.Accent);
                    code = RunFfmpegImage(inputFile, outPath, idx == 0 ? 1 : 3);
                }
                else
                {
                    Status("Reencodificando com ffmpeg...");
                    Log("[1/4] Reencodificacao (ffmpeg)", LogKind.Accent);
                    int crf = idx == 0 ? 14 : (idx == 2 ? 23 : 18);
                    string preset = idx == 0 ? "slow" : (idx == 2 ? "fast" : "medium");
                    code = RunFfmpeg(inputFile, outPath, crf, preset);
                }

                if (Cancelled) { TryDelete(outPath); LogCancel(); return null; }
                if (code != 0)
                {
                    TryDelete(outPath);
                    Log("", LogKind.Text);
                    Log("FFmpeg falhou (codigo " + code + ").", LogKind.Err);
                    Status("Erro na reencodificacao.");
                    return null;
                }
                Log("  ok", LogKind.Ok);
                Progress(70);

                // 2. ExifTool: remove metadata que ainda possa ser removida
                if (ExiftoolPath != null)
                {
                    Status("Removendo metadados residuais (exiftool)...");
                    Log("", LogKind.Text);
                    Log("[2/4] Limpeza residual (exiftool)", LogKind.Accent);
                    string eOut, eErr;
                    int ec = RunExiftool("-all= -overwrite_original \"" + outPath + "\"", out eOut, out eErr);
                    foreach (string line in SplitLines(eOut)) Log("  " + line, LogKind.Muted);
                    if (Cancelled) { TryDelete(outPath); LogCancel(); return null; }
                    if (ec != 0 && lossless)
                    {
                        // No modo sem perda a saida e uma copia crua do original:
                        // sem a passada do exiftool ela esta suja. Melhor nao
                        // entregar nada do que entregar isso.
                        foreach (string line in SplitLines(eErr)) Log("  " + line, LogKind.Err);
                        TryDelete(outPath);
                        Log("", LogKind.Text);
                        Log("ExifTool falhou (codigo " + ec + "). Saida descartada.", LogKind.Err);
                        Log("Tente o modo 'Recodificar'.", LogKind.Err);
                        Status("Erro na limpeza (exiftool).");
                        return null;
                    }
                    foreach (string line in SplitLines(eErr)) Log("  " + line, LogKind.Warn);
                    if (ec != 0)
                    {
                        // Depois de recodificar, a saida ja nasceu sem os metadados
                        // de entrada (-map_metadata -1); o exiftool e so reforco.
                        Log("  exiftool nao reescreve este formato; a saida vem da", LogKind.Warn);
                        Log("  recodificacao, que ja descarta os metadados de entrada.", LogKind.Warn);
                    }
                    else
                    {
                        Log("  ok", LogKind.Ok);
                    }

                    // Em TIFF o exiftool nao apaga o IFD0 ("Can't delete IFD0 from
                    // TIFF"): Make, Model, Software, Artist, datas etc. ficam.
                    // Segunda passada apaga tag a tag o que nao e estrutural.
                    if (ext == ".tif" || ext == ".tiff")
                    {
                        int n = StripTiffTags(outPath);
                        if (n < 0)
                        {
                            TryDelete(outPath);
                            Log("", LogKind.Text);
                            Log("Falha ao limpar as tags do TIFF. Saida descartada.", LogKind.Err);
                            Status("Erro na limpeza (TIFF).");
                            return null;
                        }
                        if (n > 0)
                            Log("  " + n + " tag(s) do IFD do TIFF removidas", LogKind.Muted);
                    }
                }
                else
                {
                    Log("", LogKind.Text);
                    Log("[2/4] exiftool ausente - etapa pulada.", LogKind.Warn);
                }

                // Ultima palavra sobre o PNG: o exiftool nao mexe em chunks
                // desconhecidos, entao a varredura roda depois dele.
                if (ext == ".png")
                {
                    int n = StripPngChunks(outPath);
                    if (n > 0)
                        Log("  " + n + " chunk(s) PNG nao essenciais removidos", LogKind.Muted);
                }
                if (Cancelled) { TryDelete(outPath); LogCancel(); return null; }
                Progress(80);

                // 3. Neutraliza timestamps do arquivo no sistema de arquivos.
                //    Isso NAO e metadata interna do video, sao atributos do FS.
                Status("Neutralizando datas do arquivo...");
                Log("", LogKind.Text);
                Log(IsWindows ? "[3/4] Datas do arquivo (NTFS)" : "[3/4] Datas e atributos do arquivo", LogKind.Accent);
                try
                {
                    DateTime neutral = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Local);
                    File.SetLastWriteTime(outPath, neutral);
                    File.SetLastAccessTime(outPath, neutral);
                    // Data de criacao so existe como atributo gravavel no Windows/macOS;
                    // no Linux a chamada e ignorada pelo runtime.
                    try { File.SetCreationTime(outPath, neutral); } catch { }
                    Log("  criacao / modificacao / acesso = 2000-01-01 00:00:00", LogKind.Muted);
                    Log("  ok", LogKind.Ok);
                }
                catch (Exception ex)
                {
                    Log("  nao foi possivel alterar as datas: " + ex.Message, LogKind.Warn);
                }
                if (!IsWindows)
                {
                    // macOS guarda a URL de origem (kMDItemWhereFroms) e a quarentena em
                    // atributos estendidos; e o equivalente do Zone.Identifier do NTFS.
                    // "xattr -c" limpa todos. No Linux o comando pode nao existir: ignora.
                    string xo, xe;
                    try
                    {
                        int xc = RunCapture("xattr", "-c \"" + outPath + "\"", out xo, out xe);
                        if (xc == 0) Log("  atributos estendidos (xattr) removidos", LogKind.Muted);
                    }
                    catch { }
                }
                Progress(88);

                // 4. Auditoria
                Log("", LogKind.Text);
                Log("[4/4] Auditoria de metadados", LogKind.Accent);
                if (ExiftoolPath != null)
                {
                    Status("Auditando...");
                    string aOut, aErr;
                    RunExiftool("-G1 -a -s \"" + outPath + "\"", out aOut, out aErr);
                    string[] metadata = SplitLines(aOut);

                    Log("", LogKind.Text);
                    foreach (string line in metadata) Log("  " + line, LogKind.Muted);

                    List<string> found = new List<string>();
                    foreach (string line in metadata)
                    {
                        foreach (string pat in SensitivePatterns)
                        {
                            if (line.IndexOf(pat, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                if (!found.Contains(line)) found.Add(line);
                                break;
                            }
                        }
                    }

                    Log("", LogKind.Text);
                    Log("CAMPOS POTENCIALMENTE SENSIVEIS", LogKind.Accent);
                    Log("", LogKind.Text);
                    if (found.Count == 0)
                    {
                        Log("  Nenhum campo sensivel obvio encontrado.", LogKind.Ok);
                    }
                    else
                    {
                        found.Sort(StringComparer.OrdinalIgnoreCase);
                        Log("  ATENCAO: revise os seguintes campos:", LogKind.Warn);
                        Log("", LogKind.Text);
                        foreach (string line in found) Log("  " + line, LogKind.Warn);
                    }
                }
                else
                {
                    Log("  exiftool ausente - auditoria pulada.", LogKind.Warn);
                }

                Progress(100);
                result = outPath;

                Log("", LogKind.Text);
                Log("RESULTADO", LogKind.Accent);
                Log(outPath, LogKind.Ok);
            }
            catch (Exception ex)
            {
                Log("", LogKind.Text);
                Log("Erro inesperado: " + ex.Message, LogKind.Err);
                Status("Erro.");
            }
            finally
            {
                current = null;
            }
            return result;
        }

        void LogCancel()
        {
            Log("", LogKind.Text);
            Log("Cancelado pelo usuario.", LogKind.Warn);
            Status("Cancelado.");
        }

        int RunFfmpeg(string input, string output, int crf, string preset)
        {
            StringBuilder a = new StringBuilder();
            a.Append("-hide_banner -y");
            a.Append(" -i \"").Append(input).Append("\"");
            a.Append(" -map 0:v:0 -map 0:a?");
            a.Append(" -map_metadata -1 -map_chapters -1 -sn -dn");
            a.Append(" -c:v libx264 -preset ").Append(preset).Append(" -crf ").Append(crf);
            a.Append(" -pix_fmt yuv420p");
            // libx264 com yuv420p exige largura e altura pares; um video de
            // 1081x1921 (gravacao de tela, corte manual) falharia. Corta no
            // maximo 1 pixel de cada borda em vez de reescalar o quadro todo.
            a.Append(" -vf crop=trunc(iw/2)*2:trunc(ih/2)*2");
            // O x264 grava um SEI user data com a build e TODAS as opcoes de
            // codificacao dentro do bitstream - fora do alcance do exiftool.
            // remove_types=6 descarta os NALs SEI; o video decodifica identico.
            a.Append(" -bsf:v filter_units=remove_types=6");
            a.Append(" -c:a aac -b:a 192k");
            a.Append(" -metadata title= -metadata artist= -metadata author=");
            a.Append(" -metadata comment= -metadata copyright= -metadata description=");
            a.Append(" -metadata creation_time=");
            a.Append(" -metadata:s:v:0 handler_name= -metadata:s:a:0 handler_name=");
            a.Append(" -metadata:s:v:0 encoder= -metadata:s:a:0 encoder=");
            a.Append(" -movflags +faststart");
            a.Append(" \"").Append(output).Append("\"");

            ProcessStartInfo psi = new ProcessStartInfo(FfmpegPath, a.ToString());
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.WorkingDirectory = Path.GetDirectoryName(output);

            Regex reDur = new Regex(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            Regex reTime = new Regex(@"time=\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            double[] total = new double[1];

            using (Process p = new Process())
            {
                p.StartInfo = psi;
                p.Start();
                current = p;

                Thread errT = new Thread(delegate()
                {
                    Pump(p.StandardError, delegate(string line)
                    {
                        if (total[0] <= 0)
                        {
                            Match md = reDur.Match(line);
                            if (md.Success) total[0] = ToSeconds(md);
                        }
                        Match mt = reTime.Match(line);
                        if (mt.Success && total[0] > 0)
                        {
                            double done = ToSeconds(mt);
                            double frac = done / total[0];
                            if (frac > 1) frac = 1;
                            Progress((int)(frac * 69.0));
                            Status("Reencodificando... " + (int)(frac * 100) + "%");
                        }
                        else if (line.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0
                              || line.IndexOf("Invalid", StringComparison.OrdinalIgnoreCase) >= 0
                              || line.IndexOf("No such", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Log("  " + line, LogKind.Warn);
                        }
                    });
                });
                errT.IsBackground = true;
                errT.Start();

                Thread outT = new Thread(delegate()
                {
                    Pump(p.StandardOutput, delegate(string s) { });
                });
                outT.IsBackground = true;
                outT.Start();

                p.WaitForExit();
                errT.Join(2000);
                outT.Join(2000);
                return p.ExitCode;
            }
        }

        // Chunks que fazem parte da imagem em si. Tudo o mais (tEXt, iTXt, zTXt,
        // eXIf, iCCP, pHYs, tIME e qualquer chunk privado) e descartavel.
        // acTL/fcTL/fdAT ficam para nao matar a animacao de um APNG.
        static readonly string[] PngKeep = {
            "IHDR", "PLTE", "IDAT", "tRNS", "IEND", "acTL", "fcTL", "fdAT"
        };

        // O 'exiftool -all=' NAO remove chunks PNG desconhecidos ou privados -
        // da para esconder dados num chunk 'prVt' e eles sobrevivem. Aqui o
        // arquivo e reescrito mantendo so a lista acima. Os pixels nao mudam.
        // Retorna quantos chunks foram descartados, ou -1 se nao for um PNG.
        public static int StripPngChunks(string path)
        {
            byte[] sig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 8) return -1;
            for (int i = 0; i < 8; i++)
                if (data[i] != sig[i]) return -1;

            int removed = 0;
            using (MemoryStream outMs = new MemoryStream(data.Length))
            {
                outMs.Write(sig, 0, 8);
                int pos = 8;
                while (pos + 12 <= data.Length)
                {
                    long len = ((long)data[pos] << 24) | ((long)data[pos + 1] << 16)
                             | ((long)data[pos + 2] << 8) | data[pos + 3];
                    if (len < 0 || pos + 12 + len > data.Length) break;

                    string type = Encoding.ASCII.GetString(data, pos + 4, 4);
                    int total = (int)(12 + len);

                    if (Array.IndexOf(PngKeep, type) >= 0)
                        outMs.Write(data, pos, total);
                    else
                        removed++;

                    pos += total;
                    if (type == "IEND") break;   // o que vier depois e lixo colado
                }
                File.WriteAllBytes(path, outMs.ToArray());
            }
            return removed;
        }

        // Recodifica a imagem descartando toda a metadata de entrada. Para PNG,
        // WebP, BMP e TIFF isso e lossless; so o JPEG perde alguma qualidade.
        int RunFfmpegImage(string input, string output, int q)
        {
            string ext = Path.GetExtension(output).ToLowerInvariant();

            StringBuilder a = new StringBuilder();
            a.Append("-hide_banner -y");
            a.Append(" -i \"").Append(input).Append("\"");
            a.Append(" -map 0:v:0 -map_metadata -1 -map_chapters -1 -sn -dn");
            a.Append(" -frames:v 1 -update 1");
            // Sem bitexact o encoder TIFF grava "Software: Lavc..." no IFD0.
            a.Append(" -flags +bitexact -fflags +bitexact");

            if (ext == ".jpg" || ext == ".jpeg")
                a.Append(" -q:v ").Append(q);
            else if (ext == ".webp")
                a.Append(" -lossless 1");

            a.Append(" \"").Append(output).Append("\"");

            string so, se;
            int code = RunCapture(FfmpegPath, a.ToString(), out so, out se);
            if (code != 0)
                foreach (string line in SplitLines(se)) Log("  " + line, LogKind.Warn);
            return code;
        }

        static double ToSeconds(Match m)
        {
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
            return double.Parse(m.Groups[1].Value, inv) * 3600
                 + double.Parse(m.Groups[2].Value, inv) * 60
                 + double.Parse(m.Groups[3].Value, inv);
        }

        // O ffmpeg atualiza o progresso com \r, entao ReadLine nao serve aqui.
        static void Pump(StreamReader reader, Action<string> onLine)
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                int c;
                while ((c = reader.Read()) >= 0)
                {
                    char ch = (char)c;
                    if (ch == '\r' || ch == '\n')
                    {
                        if (sb.Length > 0) { onLine(sb.ToString()); sb.Length = 0; }
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                }
                if (sb.Length > 0) onLine(sb.ToString());
            }
            catch { }
        }

        int RunExiftool(string args, out string stdout, out string stderr)
        {
            if (string.IsNullOrEmpty(PerlPath))
                return RunCapture(ExiftoolPath, args, out stdout, out stderr);
            return RunCapture(PerlPath, "\"" + ExiftoolPath + "\" " + args, out stdout, out stderr);
        }

        static bool IsWindows
        {
            get { return Path.DirectorySeparatorChar == '\\'; }
        }

        int RunCapture(string exe, string args, out string stdout, out string stderr)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            using (Process p = new Process())
            {
                p.StartInfo = psi;
                // Ler stdout ate o fim e SO DEPOIS o stderr trava se o processo
                // encher o buffer do stderr (4 KB) antes de terminar: ele bloqueia
                // na escrita, nos bloqueamos na leitura. stderr vai em evento.
                StringBuilder err = new StringBuilder();
                p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs ev)
                {
                    if (ev.Data != null) lock (err) err.AppendLine(ev.Data);
                };
                p.Start();
                current = p;
                p.BeginErrorReadLine();
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                stdout = o;
                lock (err) stderr = err.ToString();
                return p.ExitCode;
            }
        }

        // Copia apenas o fluxo de dados principal do arquivo (sem os alternate
        // data streams do NTFS). O destino e sempre um nome novo.
        static void CopyDataOnly(string src, string dst)
        {
            using (FileStream i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream o = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None))
                i.CopyTo(o);
        }

        // Tags que descrevem a estrutura da imagem TIFF (TIFF 6.0 + extensoes
        // comuns). Qualquer outra tag em IFD0/IFD1/SubIFD e apagada pelo
        // StripTiffTags. Nomes como o exiftool -s os imprime.
        static readonly HashSet<string> TiffStructural = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "SubfileType", "OldSubfileType", "ImageWidth", "ImageHeight", "BitsPerSample",
            "Compression", "PhotometricInterpretation", "Thresholding", "CellWidth",
            "CellLength", "FillOrder", "StripOffsets", "Orientation", "SamplesPerPixel",
            "RowsPerStrip", "StripByteCounts", "MinSampleValue", "MaxSampleValue",
            "XResolution", "YResolution", "PlanarConfiguration", "XPosition", "YPosition",
            "FreeOffsets", "FreeByteCounts", "GrayResponseUnit", "GrayResponseCurve",
            "T4Options", "T6Options", "ResolutionUnit", "TransferFunction", "Predictor",
            "WhitePoint", "PrimaryChromaticities", "ColorMap", "HalftoneHints", "TileWidth",
            "TileLength", "TileOffsets", "TileByteCounts", "SubIFD", "InkSet", "InkNames",
            "NumberofInks", "DotRange", "ExtraSamples", "SampleFormat", "SMinSampleValue",
            "SMaxSampleValue", "TransferRange", "Indexed", "JPEGTables", "OPIProxy",
            "JPEGProc", "ThumbnailOffset", "ThumbnailLength", "JPEGRestartInterval",
            "JPEGLosslessPredictors", "JPEGPointTransforms", "JPEGQTables", "JPEGDCTables",
            "JPEGACTables", "YCbCrCoefficients", "YCbCrSubSampling", "YCbCrPositioning",
            "ReferenceBlackWhite", "ImageDepth", "TileDepth", "StripRowCounts", "ImageLayer",
            "PreviewImageStart", "PreviewImageLength", "JpgFromRawStart", "JpgFromRawLength",
            "OtherImageStart", "OtherImageLength", "Decode", "DefaultImageColor"
        };

        static readonly Regex TiffTagLine = new Regex(
            @"^\[(IFD\d+|SubIFD\d*)\]\s+([A-Za-z0-9_\-]+)\s*:", RegexOptions.Compiled);

        // 'exiftool -all=' recusa apagar o IFD0 de um TIFF, porque e ali que a
        // estrutura da imagem mora. Make, Model, Software, Artist, datas e
        // qualquer tag privada conhecida sobrevivem. Aqui lista-se o que ha nos
        // IFDs e apaga-se tudo o que nao esta em TiffStructural.
        // Retorna quantas tags foram apagadas, ou -1 em erro.
        int StripTiffTags(string path)
        {
            string o, e;
            if (RunExiftool("-G1 -a -s \"" + path + "\"", out o, out e) != 0)
                return -1;

            List<string> del = new List<string>();
            foreach (string line in SplitLines(o))
            {
                Match m = TiffTagLine.Match(line);
                if (!m.Success) continue;
                string tag = m.Groups[2].Value;
                if (TiffStructural.Contains(tag)) continue;
                string arg = "-" + m.Groups[1].Value + ":" + tag + "=";
                if (!del.Contains(arg)) del.Add(arg);
            }
            if (del.Count == 0) return 0;

            int ec = RunExiftool(
                string.Join(" ", del.ToArray()) + " -overwrite_original \"" + path + "\"",
                out o, out e);
            foreach (string line in SplitLines(e)) Log("  " + line, LogKind.Warn);
            if (ec != 0) return -1;
            return del.Count;
        }

        static string[] SplitLines(string s)
        {
            if (string.IsNullOrEmpty(s)) return new string[0];
            string[] raw = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            List<string> keep = new List<string>();
            foreach (string line in raw)
                if (line.Trim().Length > 0) keep.Add(line);
            return keep.ToArray();
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
