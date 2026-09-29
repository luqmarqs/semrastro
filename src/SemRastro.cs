using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace SemRastro
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Theme.Init();
            Application.Run(new MainForm(args));
        }
    }

    // ------------------------------------------------------------
    // Runtime embutido: o ffmpeg e o exiftool viajam dentro do .exe
    // como um .zip e sao extraidos uma unica vez para o LocalAppData.
    // ------------------------------------------------------------
    static class Embedded
    {
        public const string ResourceName = "payload";

        public static string Dir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SemRastro\\runtime");
            }
        }

        static Stream OpenPayload()
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        }

        public static bool Available
        {
            get
            {
                using (Stream s = OpenPayload()) return s != null;
            }
        }

        // Extrai se ainda nao foi extraido. Retorna true se o runtime esta pronto.
        // 'report' recebe (feitos, total) para a barra de progresso.
        public static bool Ensure(Action<int, int> report)
        {
            long size;
            using (Stream probe = OpenPayload())
            {
                if (probe == null) return false;   // exe compilado sem payload
                size = probe.Length;
            }

            string dir = Dir;
            string marker = Path.Combine(dir, ".ready");
            string stamp = size.ToString();

            // Duas instancias abertas ao mesmo tempo nao podem extrair juntas.
            using (Mutex mutex = new Mutex(false, "Local\\SemRastro.runtime"))
            {
                bool held = false;
                try { held = mutex.WaitOne(TimeSpan.FromMinutes(10)); }
                catch (AbandonedMutexException) { held = true; }

                try
                {
                    if (File.Exists(marker) && File.ReadAllText(marker).Trim() == stamp)
                        return true;

                    // Extrai para um diretorio temporario e so entao promove,
                    // para nunca deixar um runtime pela metade marcado como pronto.
                    string temp = dir + ".tmp" + Process.GetCurrentProcess().Id;
                    if (Directory.Exists(temp)) Directory.Delete(temp, true);
                    Directory.CreateDirectory(temp);

                    using (Stream s = OpenPayload())
                    using (ZipArchive zip = new ZipArchive(s, ZipArchiveMode.Read))
                    {
                        int total = zip.Entries.Count;
                        int done = 0;
                        foreach (ZipArchiveEntry entry in zip.Entries)
                        {
                            string dest = Path.Combine(temp, entry.FullName.Replace('/', '\\'));
                            if (entry.Name.Length == 0)
                            {
                                Directory.CreateDirectory(dest);
                            }
                            else
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                                using (Stream es = entry.Open())
                                using (FileStream fs = File.Create(dest))
                                    es.CopyTo(fs);
                            }
                            done++;
                            if (report != null && (done % 16 == 0 || done == total))
                                report(done, total);
                        }
                    }

                    if (Directory.Exists(dir))
                    {
                        try { Directory.Delete(dir, true); }
                        catch { }
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(dir));
                    Directory.Move(temp, dir);
                    File.WriteAllText(marker, stamp);
                    return true;
                }
                finally
                {
                    if (held) mutex.ReleaseMutex();
                }
            }
        }
    }

    // ------------------------------------------------------------
    // Localizacao dos binarios externos (ffmpeg / exiftool)
    // ------------------------------------------------------------
    public static class Tools
    {
        public static string Find(string exeName)
        {
            foreach (string dir in CandidateDirs())
            {
                if (string.IsNullOrEmpty(dir) || dir.Trim().Length == 0) continue;
                string full;
                try { full = Path.Combine(dir.Trim().Trim('"'), exeName); }
                catch { continue; }
                try { if (File.Exists(full)) return full; }
                catch { }
            }
            return null;
        }

        static IEnumerable<string> CandidateDirs()
        {
            // Runtime embutido no proprio .exe tem prioridade sobre qualquer
            // instalacao do sistema, para o resultado nao variar de maquina.
            yield return Embedded.Dir;
            yield return Path.Combine(Embedded.Dir, "exiftool");

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            yield return baseDir;
            yield return Path.Combine(baseDir, "bin");
            yield return Path.Combine(baseDir, "ffmpeg");
            yield return Path.Combine(baseDir, "ffmpeg\\bin");
            yield return Path.Combine(baseDir, "exiftool");

            foreach (EnvironmentVariableTarget scope in new EnvironmentVariableTarget[] {
                EnvironmentVariableTarget.Process,
                EnvironmentVariableTarget.User,
                EnvironmentVariableTarget.Machine })
            {
                string path = null;
                try { path = Environment.GetEnvironmentVariable("PATH", scope); }
                catch { }
                if (string.IsNullOrEmpty(path)) continue;
                foreach (string d in path.Split(';')) yield return d;
            }

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(local, "Microsoft\\WinGet\\Links");
            yield return Path.Combine(local, "Programs\\ExifTool");
            yield return "C:\\Program Files\\ExifTool";
            yield return "C:\\ffmpeg\\bin";

            // Instaladores per-user (o exiftool cai aqui) - 2 niveis
            foreach (string d in Descend(Path.Combine(local, "Programs"), 2))
                yield return d;

            // Pacotes portateis do winget (o ffmpeg cai aqui) - 3 niveis
            foreach (string d in Descend(Path.Combine(local, "Microsoft\\WinGet\\Packages"), 3))
                yield return d;
        }

        // Lista as subpastas de 'root' ate 'depth' niveis, sem estourar em erro de acesso.
        static List<string> Descend(string root, int depth)
        {
            List<string> found = new List<string>();
            List<string> level = new List<string>();
            level.Add(root);

            for (int i = 0; i < depth; i++)
            {
                List<string> next = new List<string>();
                foreach (string dir in level)
                {
                    try
                    {
                        if (!Directory.Exists(dir)) continue;
                        foreach (string sub in Directory.GetDirectories(dir))
                        {
                            found.Add(sub);
                            next.Add(sub);
                        }
                    }
                    catch { }
                }
                level = next;
            }
            return found;
        }
    }

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
                    int ec = RunCapture(ExiftoolPath,
                        "-all= -overwrite_original \"" + outPath + "\"", out eOut, out eErr);
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

                // 3. Neutraliza timestamps do arquivo no Windows.
                //    Isso NAO e metadata interna do video, sao atributos do NTFS.
                Status("Neutralizando datas do arquivo...");
                Log("", LogKind.Text);
                Log("[3/4] Datas do arquivo (NTFS)", LogKind.Accent);
                try
                {
                    DateTime neutral = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Local);
                    File.SetCreationTime(outPath, neutral);
                    File.SetLastWriteTime(outPath, neutral);
                    File.SetLastAccessTime(outPath, neutral);
                    Log("  criacao / modificacao / acesso = 2000-01-01 00:00:00", LogKind.Muted);
                    Log("  ok", LogKind.Ok);
                }
                catch (Exception ex)
                {
                    Log("  nao foi possivel alterar as datas: " + ex.Message, LogKind.Warn);
                }
                Progress(88);

                // 4. Auditoria
                Log("", LogKind.Text);
                Log("[4/4] Auditoria de metadados", LogKind.Accent);
                if (ExiftoolPath != null)
                {
                    Status("Auditando...");
                    string aOut, aErr;
                    RunCapture(ExiftoolPath, "-G1 -a -s \"" + outPath + "\"", out aOut, out aErr);
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
            if (RunCapture(ExiftoolPath, "-G1 -a -s \"" + path + "\"", out o, out e) != 0)
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

            int ec = RunCapture(ExiftoolPath,
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

    // ------------------------------------------------------------
    // Tema: segue o claro/escuro e a cor de destaque do Windows.
    // Paleta e tipografia do Fluent 2 (Windows 11).
    // ------------------------------------------------------------
    static class Theme
    {
        public static bool Dark;
        public static double Dpi = 96;
        public static Color Window, Card, CardHover, Layer, LayerHover, Border, BorderStrong;
        public static Color Text, TextSecondary, TextTertiary, TextDisabled;
        public static Color Accent, AccentHover, AccentPressed, AccentText, AccentSoft;
        public static Color Ok, Warn, Err, LogBg;
        public static Font Title, Body, BodyStrong, Caption, CaptionStrong, Icon, IconLarge, Mono;

        public static void Init()
        {
            Dark = ReadDark();
            Dpi = Native.SystemDpi();

            Color sys = ReadAccent();
            if (Dark)
            {
                Window = Color.FromArgb(32, 32, 32);
                Card = Color.FromArgb(43, 43, 43);
                CardHover = Color.FromArgb(50, 50, 50);
                Layer = Color.FromArgb(38, 38, 38);
                LayerHover = Color.FromArgb(56, 56, 56);
                Border = Color.FromArgb(58, 58, 58);
                BorderStrong = Color.FromArgb(96, 96, 96);
                Text = Color.FromArgb(255, 255, 255);
                TextSecondary = Color.FromArgb(200, 200, 200);
                TextTertiary = Color.FromArgb(140, 140, 140);
                TextDisabled = Color.FromArgb(110, 110, 110);
                // No tema escuro o Windows usa um tom mais claro do accent com
                // texto escuro (ex.: botao "Instalar" da Microsoft Store).
                Accent = Mix(sys, Color.White, 0.38);
                AccentHover = Mix(sys, Color.White, 0.46);
                AccentPressed = Mix(sys, Color.White, 0.30);
                AccentText = Color.FromArgb(0, 0, 0);
                AccentSoft = Color.FromArgb(28, Accent);
                Ok = Color.FromArgb(108, 203, 95);
                Warn = Color.FromArgb(252, 225, 0);
                Err = Color.FromArgb(255, 153, 164);
                LogBg = Color.FromArgb(24, 24, 24);
            }
            else
            {
                Window = Color.FromArgb(243, 243, 243);
                Card = Color.FromArgb(255, 255, 255);
                CardHover = Color.FromArgb(249, 249, 249);
                Layer = Color.FromArgb(251, 251, 251);
                LayerHover = Color.FromArgb(240, 240, 240);
                Border = Color.FromArgb(229, 229, 229);
                BorderStrong = Color.FromArgb(200, 200, 200);
                Text = Color.FromArgb(27, 27, 27);
                TextSecondary = Color.FromArgb(93, 93, 93);
                TextTertiary = Color.FromArgb(138, 138, 138);
                TextDisabled = Color.FromArgb(160, 160, 160);
                Accent = sys;
                AccentHover = Mix(sys, Color.White, 0.10);
                AccentPressed = Mix(sys, Color.Black, 0.10);
                AccentText = Color.White;
                AccentSoft = Color.FromArgb(22, Accent);
                Ok = Color.FromArgb(15, 123, 15);
                Warn = Color.FromArgb(157, 93, 0);
                Err = Color.FromArgb(196, 43, 28);
                LogBg = Color.FromArgb(250, 250, 250);
            }

            Title = Make(new string[] { "Segoe UI Variable Display Semibold", "Segoe UI Semibold", "Segoe UI" }, 15f, FontStyle.Bold);
            Body = Make(new string[] { "Segoe UI Variable Text", "Segoe UI" }, 10f, FontStyle.Regular);
            BodyStrong = Make(new string[] { "Segoe UI Variable Text Semibold", "Segoe UI Semibold", "Segoe UI" }, 10f, FontStyle.Bold);
            Caption = Make(new string[] { "Segoe UI Variable Small", "Segoe UI" }, 8.75f, FontStyle.Regular);
            CaptionStrong = Make(new string[] { "Segoe UI Variable Small Semibold", "Segoe UI Semibold", "Segoe UI" }, 8.75f, FontStyle.Bold);
            Icon = Make(new string[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }, 11f, FontStyle.Regular);
            IconLarge = Make(new string[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }, 26f, FontStyle.Regular);
            Mono = Make(new string[] { "Cascadia Mono", "Consolas" }, 9f, FontStyle.Regular);
        }

        static bool ReadDark()
        {
            // SEMRASTRO_THEME=dark|light forca o tema (util para testes/screenshot).
            string force = Environment.GetEnvironmentVariable("SEMRASTRO_THEME");
            if (force == "dark") return true;
            if (force == "light") return false;
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("AppsUseLightTheme");
                        if (v is int) return (int)v == 0;
                    }
                }
            }
            catch { }
            return false;
        }

        static Color ReadAccent()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Accent"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("AccentColorMenu");
                        if (v is int)
                        {
                            // ABGR
                            uint u = (uint)(int)v;
                            return Color.FromArgb((int)(u & 0xFF), (int)((u >> 8) & 0xFF), (int)((u >> 16) & 0xFF));
                        }
                    }
                }
            }
            catch { }
            return Color.FromArgb(0, 120, 212);
        }

        static Font Make(string[] families, float size, FontStyle fallbackStyle)
        {
            using (InstalledFontCollection ifc = new InstalledFontCollection())
            {
                foreach (string name in families)
                {
                    foreach (FontFamily f in ifc.Families)
                    {
                        if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            // As variantes "Semibold" ja carregam o peso no nome:
                            // pedir Bold em cima delas engrossaria demais.
                            FontStyle st = name.IndexOf("Semib", StringComparison.OrdinalIgnoreCase) >= 0
                                ? FontStyle.Regular : fallbackStyle;
                            if (!f.IsStyleAvailable(st)) st = FontStyle.Regular;
                            return new Font(f, size, st, GraphicsUnit.Point);
                        }
                    }
                }
            }
            return new Font("Segoe UI", size, fallbackStyle, GraphicsUnit.Point);
        }

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (radius <= 0 || r.Width <= 0 || r.Height <= 0) { p.AddRectangle(r); return p; }
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    static class Native
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hWnd, string app, string idList);

        // Barra de titulo na cor da janela (Windows 11) e modo escuro (Windows 10 20H1+).
        public static void StyleTitleBar(IntPtr hwnd, bool dark, Color caption, Color text)
        {
            try
            {
                int on = dark ? 1 : 0;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref on, 4);
                int cap = caption.R | (caption.G << 8) | (caption.B << 16);
                DwmSetWindowAttribute(hwnd, 35, ref cap, 4);   // DWMWA_CAPTION_COLOR
                int txt = text.R | (text.G << 8) | (text.B << 16);
                DwmSetWindowAttribute(hwnd, 36, ref txt, 4);   // DWMWA_TEXT_COLOR
            }
            catch { }
        }

        [DllImport("user32.dll")] static extern uint GetDpiForSystem();
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr hdc, int index);

        // DPI do sistema como o processo (DPI-aware pelo manifesto) o enxerga.
        // Control.DeviceDpi e o GDI+ devolvem 96 no .NET Framework sem app.config.
        public static double SystemDpi()
        {
            try { uint d = GetDpiForSystem(); if (d >= 72) return d; }
            catch { }
            try
            {
                IntPtr dc = GetDC(IntPtr.Zero);
                int d = GetDeviceCaps(dc, 88);   // LOGPIXELSX
                ReleaseDC(IntPtr.Zero, dc);
                if (d >= 72) return d;
            }
            catch { }
            return 96;
        }

        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        public static void ScrollToBottom(Control c)
        {
            try { SendMessage(c.Handle, 0x115, (IntPtr)7, IntPtr.Zero); }   // WM_VSCROLL, SB_BOTTOM
            catch { }
        }

        public static void DarkScrollbars(Control c)
        {
            try { SetWindowTheme(c.Handle, Theme.Dark ? "DarkMode_Explorer" : "Explorer", null); }
            catch { }
        }
    }

    // ------------------------------------------------------------
    // Controles desenhados a mao (Fluent)
    // ------------------------------------------------------------
    abstract class Drawn : Control
    {
        protected bool hover, down;

        protected Drawn()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected int P(double v) { return (int)Math.Round(v * Theme.Dpi / 96.0); }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected static void Prep(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        }
    }

    enum ButtonStyle { Primary, Secondary, Subtle, Danger }

    class FluentButton : Drawn
    {
        public ButtonStyle Style = ButtonStyle.Secondary;
        public string Glyph;

        public FluentButton()
        {
            Cursor = Cursors.Hand;
            Font = Theme.Body;
            AccessibleRole = AccessibleRole.PushButton;
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
        }

        public void PerformClick()
        {
            if (Enabled && Visible) OnClick(EventArgs.Empty);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            AccessibleName = Text;
            Invalidate();
            base.OnTextChanged(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { PerformClick(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        // Expoe "Invoke"/DoDefaultAction: leitores de tela e testes de UI
        // Automation conseguem acionar o botao sem simular o mouse.
        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new ButtonAccessible(this);
        }

        class ButtonAccessible : ControlAccessibleObject
        {
            readonly FluentButton owner;
            public ButtonAccessible(FluentButton b) : base(b) { owner = b; }
            public override AccessibleRole Role { get { return AccessibleRole.PushButton; } }
            public override string Name { get { return owner.Text; } set { } }
            public override string DefaultAction { get { return "Press"; } }
            public override AccessibleStates State
            {
                get
                {
                    AccessibleStates st = base.State;
                    if (!owner.Enabled) st |= AccessibleStates.Unavailable;
                    return st;
                }
            }
            public override void DoDefaultAction() { owner.PerformClick(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);

            Color fill, text, border = Color.Empty;
            if (!Enabled)
            {
                fill = Style == ButtonStyle.Subtle ? Color.Transparent : Theme.Layer;
                text = Theme.TextDisabled;
                if (Style != ButtonStyle.Subtle) border = Theme.Border;
            }
            else if (Style == ButtonStyle.Primary)
            {
                fill = down ? Theme.AccentPressed : (hover ? Theme.AccentHover : Theme.Accent);
                text = Theme.AccentText;
            }
            else if (Style == ButtonStyle.Danger)
            {
                Color baseC = Theme.Dark ? Color.FromArgb(120, 40, 40) : Color.FromArgb(196, 43, 28);
                fill = down ? Theme.Mix(baseC, Color.Black, 0.1) : (hover ? Theme.Mix(baseC, Color.White, 0.08) : baseC);
                text = Color.White;
            }
            else if (Style == ButtonStyle.Subtle)
            {
                fill = down ? Theme.LayerHover : (hover ? Theme.Layer : Color.Transparent);
                text = hover ? Theme.Text : Theme.TextSecondary;
            }
            else
            {
                fill = down ? Theme.LayerHover : (hover ? Theme.CardHover : Theme.Card);
                text = Theme.Text;
                border = Theme.BorderStrong;
            }

            using (GraphicsPath path = Theme.RoundRect(r, P(4)))
            {
                if (fill.A > 0) using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                if (border != Color.Empty) using (Pen p = new Pen(border)) g.DrawPath(p, path);
            }
            if (Focused && ShowFocusCues)
            {
                Rectangle fr = new Rectangle(-P(3), -P(3), Width + P(5), Height + P(5));
                using (GraphicsPath fp = Theme.RoundRect(fr, P(6)))
                using (Pen p = new Pen(Theme.Text, P(1.5)))
                    g.DrawPath(p, fp);
            }

            TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            Font f = Style == ButtonStyle.Primary ? Theme.BodyStrong : Font;
            if (string.IsNullOrEmpty(Glyph))
            {
                TextRenderer.DrawText(g, Text, f, ClientRectangle, text, flags);
            }
            else
            {
                Size ts = TextRenderer.MeasureText(g, Text, f, Size.Empty, flags);
                Size gs = TextRenderer.MeasureText(g, Glyph, Theme.Icon, Size.Empty, flags);
                int gap = P(8);
                int total = gs.Width + gap + ts.Width;
                int x = (Width - total) / 2;
                Rectangle gr = new Rectangle(x, 0, gs.Width, Height);
                Rectangle tr = new Rectangle(x + gs.Width + gap, 0, ts.Width, Height);
                TextRenderer.DrawText(g, Glyph, Theme.Icon, gr, text, flags);
                TextRenderer.DrawText(g, Text, f, tr, text, flags);
            }
        }
    }

    // Grupo de opcoes exclusivas, no estilo dos "segmented controls".
    class Segmented : Drawn
    {
        string[] items = new string[0];
        int selected = -1;
        int hoverIdx = -1;

        public event EventHandler SelectedIndexChanged;

        public string[] Items
        {
            get { return items; }
            set { items = value ?? new string[0]; Invalidate(); }
        }

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (value == selected) return;
                selected = value;
                AccessibleDescription = selected >= 0 && selected < items.Length ? items[selected] : "";
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        public Segmented()
        {
            Cursor = Cursors.Hand;
            Font = Theme.Body;
            AccessibleRole = AccessibleRole.List;
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Left || keyData == Keys.Right) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!Enabled || items.Length == 0) { base.OnKeyDown(e); return; }
            if (e.KeyCode == Keys.Left && selected > 0) { SelectedIndex = selected - 1; e.Handled = true; }
            else if (e.KeyCode == Keys.Right && selected < items.Length - 1) { SelectedIndex = selected + 1; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        int IndexAt(int x)
        {
            if (items.Length == 0) return -1;
            int w = Width / items.Length;
            int i = x / Math.Max(1, w);
            return i < 0 || i >= items.Length ? -1 : i;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.X);
            if (i != hoverIdx) { hoverIdx = i; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hoverIdx = -1; base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (!Enabled) return;
            int i = IndexAt(e.X);
            if (i >= 0) SelectedIndex = i;
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.RoundRect(r, P(4)))
            using (SolidBrush b = new SolidBrush(Theme.Layer))
            using (Pen p = new Pen(Theme.Border))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
            if (Focused && ShowFocusCues)
            {
                Rectangle fr = new Rectangle(-P(3), -P(3), Width + P(5), Height + P(5));
                using (GraphicsPath fp = Theme.RoundRect(fr, P(6)))
                using (Pen p = new Pen(Theme.Text, P(1.5)))
                    g.DrawPath(p, fp);
            }
            if (items.Length == 0) return;

            int pad = P(3);
            float w = (Width - pad * 2) / (float)items.Length;
            for (int i = 0; i < items.Length; i++)
            {
                Rectangle cell = new Rectangle((int)Math.Round(pad + i * w), pad,
                    (int)Math.Round(w) - 1, Height - pad * 2 - 1);
                bool sel = i == selected;
                Color text;
                if (!Enabled) text = Theme.TextDisabled;
                else if (sel) text = Theme.Text;
                else text = Theme.TextSecondary;

                if (sel || (Enabled && i == hoverIdx))
                {
                    Color fill = sel ? Theme.Card : Theme.LayerHover;
                    if (sel && Theme.Dark) fill = Theme.Mix(Theme.Card, Color.White, 0.06);
                    using (GraphicsPath cp = Theme.RoundRect(cell, P(3)))
                    using (SolidBrush b = new SolidBrush(fill))
                    {
                        g.FillPath(b, cp);
                        if (sel)
                            using (Pen p = new Pen(Theme.BorderStrong)) g.DrawPath(p, cp);
                    }
                }
                TextRenderer.DrawText(g, items[i], sel ? Theme.BodyStrong : Font, cell, text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
        }
    }

    // Area de soltar arquivos. Expandida (estado vazio) ou compacta (com fila).
    class DropZone : Drawn
    {
        public bool Compact;
        public bool Active;   // arrastando por cima

        public DropZone()
        {
            Cursor = Cursors.Hand;
            AllowDrop = true;
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            Active = true; Invalidate(); base.OnDragEnter(e);
        }

        protected override void OnDragLeave(EventArgs e)
        {
            Active = false; Invalidate(); base.OnDragLeave(e);
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            Active = false; Invalidate(); base.OnDragDrop(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            bool hi = Active || hover;
            using (GraphicsPath path = Theme.RoundRect(r, P(8)))
            {
                using (SolidBrush b = new SolidBrush(Active ? Theme.AccentSoft : (hover ? Theme.CardHover : Theme.Card)))
                    g.FillPath(b, path);
                using (Pen p = new Pen(hi ? Theme.Accent : Theme.BorderStrong, Active ? P(1.5) : 1))
                {
                    if (!Active) p.DashPattern = new float[] { 4f, 3f };
                    g.DrawPath(p, path);
                }
            }

            TextFormatFlags c = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            Color glyphColor = hi ? Theme.Accent : Theme.TextTertiary;
            string title = Active ? "Pode soltar" : "Arraste vídeos ou imagens aqui";
            string sub = Compact ? "ou clique para adicionar mais" : "ou clique para escolher  ·  pode soltar vários de uma vez, ou uma pasta inteira";

            if (Compact)
            {
                int x = P(20);
                Rectangle gr = new Rectangle(x, 0, P(28), Height);
                TextRenderer.DrawText(g, "\uE8E5", Theme.IconLarge, gr, glyphColor, c);
                Rectangle tr = new Rectangle(x + P(44), 0, Width - x - P(44), Height);
                Size ts = TextRenderer.MeasureText(g, title, Theme.BodyStrong, Size.Empty, c);
                TextRenderer.DrawText(g, title, Theme.BodyStrong, tr, Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                Rectangle sr = new Rectangle(tr.X + ts.Width + P(12), 0, Width - tr.X - ts.Width - P(150), Height);
                TextRenderer.DrawText(g, sub, Theme.Caption, sr, Theme.TextTertiary,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
            else
            {
                int cy = Height / 2;
                Rectangle gr = new Rectangle(0, cy - P(58), Width, P(44));
                TextRenderer.DrawText(g, "\uE8E5", Theme.IconLarge, gr, glyphColor, c);
                Rectangle tr = new Rectangle(0, cy - P(8), Width, P(26));
                TextRenderer.DrawText(g, title, Theme.BodyStrong, tr, Theme.Text, c);
                Rectangle sr = new Rectangle(P(16), cy + P(18), Width - P(32), P(22));
                TextRenderer.DrawText(g, sub, Theme.Caption, sr, Theme.TextTertiary, c | TextFormatFlags.EndEllipsis);
            }
        }
    }

    enum RowState { Pending, Running, Done, Error, Cancelled }

    // Uma linha da fila de arquivos.
    class FileRow : Drawn
    {
        public string FilePath;
        public bool IsImage;
        public string Meta = "";
        public string Detail = "";     // ex.: nome do arquivo de saida
        public RowState State = RowState.Pending;
        public int Percent;
        public bool CanRemove = true;

        public event EventHandler RemoveClicked;
        Rectangle removeRect;
        bool hoverRemove;
        Font typeFont, smallIcon;

        public FileRow()
        {
            Cursor = Cursors.Default;
            Height = 52;
            typeFont = new Font(Theme.Icon.FontFamily, 14f, GraphicsUnit.Point);
            smallIcon = new Font(Theme.Icon.FontFamily, 9f, GraphicsUnit.Point);
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleRole = AccessibleRole.ListItem;
        }

        protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        // Delete tira o arquivo da fila (equivalente ao "x" da linha).
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete && CanRemove && RemoveClicked != null)
            {
                RemoveClicked(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = removeRect.Contains(e.Location);
            if (h != hoverRemove) { hoverRemove = h; Invalidate(); }
            Cursor = h && CanRemove ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (CanRemove && removeRect.Contains(e.Location) && RemoveClicked != null)
                RemoveClicked(this, EventArgs.Empty);
            base.OnMouseClick(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            FileList l = Parent as FileList;
            if (l != null) l.Wheel(e.Delta);
            base.OnMouseWheel(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);
            if (hover || Focused)
                using (SolidBrush b = new SolidBrush(Theme.LayerHover))
                    g.FillRectangle(b, new Rectangle(P(4), P(2), Width - P(8), Height - P(4)));

            TextFormatFlags l = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
            TextFormatFlags c = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

            // icone do tipo
            Rectangle gr = new Rectangle(P(16), 0, P(24), Height);
            TextRenderer.DrawText(g, IsImage ? "\uE91B" : "\uE714", typeFont, gr, Theme.TextSecondary, c);

            // status a direita
            string status; Color sc; string glyph = null;
            switch (State)
            {
                case RowState.Running: status = Percent + "%"; sc = Theme.Text; break;
                case RowState.Done: status = "Limpo"; sc = Theme.Ok; glyph = "\uE930"; break;
                case RowState.Error: status = "Erro"; sc = Theme.Err; glyph = "\uEA39"; break;
                case RowState.Cancelled: status = "Cancelado"; sc = Theme.TextTertiary; glyph = "\uE711"; break;
                default: status = "Na fila"; sc = Theme.TextTertiary; break;
            }
            int right = Width - P(16);
            removeRect = Rectangle.Empty;
            if (CanRemove)
            {
                removeRect = new Rectangle(right - P(28), (Height - P(28)) / 2, P(28), P(28));
                if (hover)
                {
                    if (hoverRemove)
                        using (GraphicsPath rp = Theme.RoundRect(removeRect, P(4)))
                        using (SolidBrush b = new SolidBrush(Theme.CardHover))
                            g.FillPath(b, rp);
                    TextRenderer.DrawText(g, "\uE711", smallIcon, removeRect,
                        hoverRemove ? Theme.Text : Theme.TextTertiary, c);
                }
                right = removeRect.Left - P(8);
            }
            Size ss = TextRenderer.MeasureText(g, status, Theme.CaptionStrong, Size.Empty, c);
            Rectangle sr = new Rectangle(right - ss.Width, 0, ss.Width, Height);
            TextRenderer.DrawText(g, status, Theme.CaptionStrong, sr, sc, c);
            int statusLeft = sr.Left;
            if (glyph != null)
            {
                Rectangle gg = new Rectangle(sr.Left - P(22), 0, P(18), Height);
                TextRenderer.DrawText(g, glyph, Theme.Icon, gg, sc, c);
                statusLeft = gg.Left;
            }

            // nome + meta
            int x = P(52);
            int wText = statusLeft - P(16) - x;
            string name = Path.GetFileName(FilePath);
            Rectangle nr = new Rectangle(x, P(9), wText, P(20));
            TextRenderer.DrawText(g, name, Theme.Body, nr, Theme.Text, l);
            string meta = Meta;
            if (State == RowState.Done && Detail.Length > 0) meta = "salvo como " + Detail + "   ·   na mesma pasta";
            else if ((State == RowState.Error || State == RowState.Cancelled) && Detail.Length > 0) meta = Detail;
            Rectangle mr = new Rectangle(x, P(29), wText, P(18));
            TextRenderer.DrawText(g, meta, Theme.Caption, mr,
                State == RowState.Error ? Theme.Err : Theme.TextTertiary, l);

            // progresso do arquivo: linha fina no rodape da linha
            if (State == RowState.Running)
            {
                int w = (int)((Width - P(32)) * Math.Min(100, Percent) / 100.0);
                Rectangle track = new Rectangle(P(16), Height - P(4), Width - P(32), P(2));
                using (SolidBrush b = new SolidBrush(Theme.Border)) g.FillRectangle(b, track);
                if (w > 0)
                    using (SolidBrush b = new SolidBrush(Theme.Accent))
                        g.FillRectangle(b, new Rectangle(track.X, track.Y, w, track.Height));
            }
        }
    }

    // Painel com fundo arredondado e borda; hospeda a lista de linhas.
    class Card : Panel
    {
        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush bg = new SolidBrush(Parent != null ? Parent.BackColor : Theme.Window))
                g.FillRectangle(bg, ClientRectangle);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            int rad = (int)Math.Round(8.0 * Theme.Dpi / 96.0);
            using (GraphicsPath path = Theme.RoundRect(r, rad))
            using (SolidBrush b = new SolidBrush(Theme.Card))
            using (Pen p = new Pen(Theme.Border))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
        }
    }

    // Lista rolavel de FileRow. Rolagem propria (VScrollBar) em vez de
    // AutoScroll: o AutoScroll insiste numa barra horizontal quando a
    // vertical aparece, e nao ha como desliga-la de forma confiavel.
    class FileList : Panel
    {
        public readonly List<FileRow> Rows = new List<FileRow>();
        readonly VScrollBar bar;
        int offset;

        public FileList()
        {
            BackColor = Theme.Card;
            DoubleBuffered = true;
            bar = new VScrollBar();
            bar.Visible = false;
            bar.ValueChanged += delegate { offset = bar.Value; Relayout(); };
            bar.HandleCreated += delegate { Native.DarkScrollbars(bar); };
            Controls.Add(bar);
        }

        int RowHeight { get { return (int)Math.Round(52.0 * Theme.Dpi / 96.0); } }
        int Pad { get { return (int)Math.Round(4.0 * Theme.Dpi / 96.0); } }

        public void Add(FileRow row)
        {
            Rows.Add(row);
            Controls.Add(row);
            Relayout();
        }

        public void Remove(FileRow row)
        {
            Rows.Remove(row);
            Controls.Remove(row);
            row.Dispose();
            Relayout();
        }

        public void Clear()
        {
            foreach (FileRow r in Rows) { Controls.Remove(r); r.Dispose(); }
            Rows.Clear();
            Relayout();
        }

        public void ScrollIntoView(FileRow row)
        {
            int i = Rows.IndexOf(row);
            if (i < 0 || !bar.Visible) return;
            int top = Pad + i * RowHeight;
            int bottom = top + RowHeight;
            if (top < offset) bar.Value = Math.Max(bar.Minimum, top);
            else if (bottom > offset + ClientSize.Height)
                bar.Value = Math.Min(bar.Maximum - bar.LargeChange + 1, bottom - ClientSize.Height);
        }

        public void Wheel(int delta)
        {
            if (!bar.Visible) return;
            int v = bar.Value - delta / 120 * RowHeight;
            int max = bar.Maximum - bar.LargeChange + 1;
            bar.Value = Math.Max(bar.Minimum, Math.Min(max, v));
        }

        protected override void OnMouseWheel(MouseEventArgs e) { Wheel(e.Delta); base.OnMouseWheel(e); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }

        public void Relayout()
        {
            SuspendLayout();
            int rh = RowHeight;
            int total = Rows.Count * rh + Pad * 2;
            bool need = total > ClientSize.Height && ClientSize.Height > 0;
            if (need)
            {
                bar.SetBounds(ClientSize.Width - bar.Width, 0, bar.Width, ClientSize.Height);
                bar.Maximum = total;
                bar.LargeChange = Math.Max(1, ClientSize.Height);
                bar.SmallChange = rh;
                int max = Math.Max(0, total - ClientSize.Height);
                if (bar.Value > max) bar.Value = max;
                bar.Visible = true;
                offset = bar.Value;
            }
            else
            {
                bar.Visible = false;
                offset = 0;
            }
            int w = ClientSize.Width - (need ? bar.Width : 0);
            int y = Pad - offset;
            foreach (FileRow r in Rows)
            {
                r.SetBounds(0, y, w, rh);
                y += rh;
            }
            ResumeLayout();
        }
    }

    // Barra de progresso fina (4 px) com cantos redondos; modo marquee.
    class ThinProgress : Drawn
    {
        int value;
        bool marquee;
        int phase;
        System.Windows.Forms.Timer timer;

        public int Value
        {
            get { return value; }
            set { this.value = Math.Max(0, Math.Min(100, value)); Invalidate(); }
        }

        public bool Marquee
        {
            get { return marquee; }
            set
            {
                marquee = value;
                if (timer == null) { timer = new System.Windows.Forms.Timer(); timer.Interval = 30; timer.Tick += delegate { phase = (phase + 3) % 130; Invalidate(); }; }
                timer.Enabled = marquee;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Prep(g);
            int h = P(4);
            Rectangle track = new Rectangle(0, (Height - h) / 2, Width, h);
            using (GraphicsPath tp = Theme.RoundRect(track, h / 2))
            using (SolidBrush b = new SolidBrush(Theme.Border))
                g.FillPath(b, tp);

            if (marquee)
            {
                int w = Width * 30 / 100;
                int x = (int)((phase - 30) / 100.0 * Width);
                Rectangle fill = new Rectangle(Math.Max(0, x), track.Y, Math.Min(w, Width - Math.Max(0, x)), h);
                if (fill.Width > 0)
                    using (GraphicsPath fp = Theme.RoundRect(fill, h / 2))
                    using (SolidBrush b = new SolidBrush(Theme.Accent))
                        g.FillPath(b, fp);
            }
            else if (value > 0)
            {
                Rectangle fill = new Rectangle(0, track.Y, Math.Max(h, Width * value / 100), h);
                using (GraphicsPath fp = Theme.RoundRect(fill, h / 2))
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                    g.FillPath(b, fp);
            }
        }
    }

    // ------------------------------------------------------------
    // Janela principal
    // ------------------------------------------------------------
    public class MainForm : Form
    {
        // cabecalho
        Label titleLabel, subtitleLabel;
        // dependencias (so aparece quando falta algo)
        Panel depPanel; Label depLabel; FluentButton depButton;
        // entrada
        DropZone dropZone;
        Card listCard; FileList list;
        FluentButton clearButton;
        // opcoes
        Label videoLabel, imageLabel;
        Segmented videoQuality, imageMode;
        // acao
        ThinProgress progress; Label statusLabel;
        FluentButton runButton, folderButton, detailsButton;
        // detalhes
        RichTextBox log;
        Label footer;
        ToolTip tips;

        string ffmpegPath, exiftoolPath;
        bool detailsOpen;
        bool preparing;
        volatile bool running;
        volatile bool cancelRequested;
        Cleaner cleaner;
        Thread worker;
        List<string> outputs = new List<string>();

        public MainForm(string[] args)
        {
            Text = "SemRastro";
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(P(820), P(640));
            MinimumSize = new Size(P(700), P(560));
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            FormClosing += OnFormClosing;
            KeyPreview = true;
            KeyDown += OnKeyDown;

            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            BuildUi();
            Relayout();

            if (args != null) AddFiles(args);
            Shown += OnShown;
        }

        int P(double v) { return (int)Math.Round(v * Theme.Dpi / 96.0); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.StyleTitleBar(Handle, Theme.Dark, Theme.Window, Theme.Text);
        }

        // ------------------------------------------------------------
        // Construcao
        // ------------------------------------------------------------
        void BuildUi()
        {
            tips = new ToolTip();
            tips.AutoPopDelay = 12000;

            titleLabel = new Label();
            titleLabel.Text = "SemRastro";
            titleLabel.Font = Theme.Title;
            titleLabel.ForeColor = Theme.Text;
            titleLabel.AutoSize = false;
            Controls.Add(titleLabel);

            subtitleLabel = new Label();
            subtitleLabel.Text = "Remove GPS, câmera, datas, software e outros rastros de vídeos e imagens. Nada sai do seu computador.";
            subtitleLabel.Font = Theme.Body;
            subtitleLabel.ForeColor = Theme.TextSecondary;
            subtitleLabel.AutoSize = false;
            subtitleLabel.AutoEllipsis = true;
            Controls.Add(subtitleLabel);

            depPanel = new Panel();
            depPanel.BackColor = Theme.Dark ? Color.FromArgb(67, 53, 25) : Color.FromArgb(255, 244, 206);
            depPanel.Visible = false;
            Controls.Add(depPanel);

            depLabel = new Label();
            depLabel.Font = Theme.Body;
            depLabel.ForeColor = Theme.Text;
            depLabel.AutoSize = false;
            depLabel.TextAlign = ContentAlignment.MiddleLeft;
            depPanel.Controls.Add(depLabel);

            depButton = new FluentButton();
            depButton.Text = "Instalar com winget";
            depButton.Style = ButtonStyle.Secondary;
            depButton.Click += OnInstallDeps;
            depPanel.Controls.Add(depButton);

            dropZone = new DropZone();
            dropZone.Click += OnPickFiles;
            dropZone.DragEnter += OnDragEnter;
            dropZone.DragDrop += OnDragDrop;
            Controls.Add(dropZone);

            listCard = new Card();
            listCard.Visible = false;
            EnableDrop(listCard);
            Controls.Add(listCard);

            list = new FileList();
            EnableDrop(list);
            listCard.Controls.Add(list);

            clearButton = new FluentButton();
            clearButton.Text = "Limpar lista";
            clearButton.Glyph = "\uE894";
            clearButton.Style = ButtonStyle.Subtle;
            clearButton.Font = Theme.Caption;
            clearButton.Visible = false;
            clearButton.Click += delegate { if (!running) { list.Clear(); outputs.Clear(); AfterQueueChanged(); } };
            dropZone.Controls.Add(clearButton);

            videoLabel = MakeCaption("Vídeo");
            videoQuality = new Segmented();
            videoQuality.Items = new string[] { "Máxima", "Alta", "Média" };
            videoQuality.SelectedIndex = 1;
            tips.SetToolTip(videoQuality,
                "Reencodificação H.264 completa. Máxima: CRF 14, lenta. Alta: CRF 18, visualmente\r\n"
                + "indistinguível do original. Média: CRF 23, rápida e menor. A saída é sempre .mp4.");
            Controls.Add(videoQuality);

            imageLabel = MakeCaption("Imagem");
            imageMode = new Segmented();
            imageMode.Items = new string[] { "Recodificar (máx.)", "Recodificar", "Sem perda" };
            imageMode.SelectedIndex = 1;
            tips.SetToolTip(imageMode,
                "Recodificar monta um arquivo novo a partir dos pixels: container limpo, garantido.\r\n"
                + "Só o JPEG perde um pouco de qualidade (PNG, WebP e TIFF são sem perda).\r\n"
                + "Sem perda mantém os pixels bit a bit e só remove os metadados.\r\n"
                + "HEIC, AVIF e GIF usam sempre 'Sem perda'.");
            Controls.Add(imageMode);

            progress = new ThinProgress();
            Controls.Add(progress);

            statusLabel = new Label();
            statusLabel.Font = Theme.Caption;
            statusLabel.ForeColor = Theme.TextSecondary;
            statusLabel.AutoSize = false;
            statusLabel.AutoEllipsis = true;
            statusLabel.Text = "Pronto.";
            Controls.Add(statusLabel);

            detailsButton = new FluentButton();
            detailsButton.Text = "Detalhes";
            detailsButton.Glyph = "\uE70D";
            detailsButton.Style = ButtonStyle.Subtle;
            detailsButton.Click += delegate
            {
                detailsOpen = !detailsOpen;
                detailsButton.Glyph = detailsOpen ? "\uE70E" : "\uE70D";
                Relayout();
                if (detailsOpen) Post(delegate { Native.ScrollToBottom(log); });
            };
            Controls.Add(detailsButton);

            folderButton = new FluentButton();
            folderButton.Text = "Abrir pasta";
            folderButton.Glyph = "\uE838";
            folderButton.Style = ButtonStyle.Secondary;
            folderButton.Enabled = false;
            folderButton.Click += OnOpenFolder;
            Controls.Add(folderButton);

            runButton = new FluentButton();
            runButton.Text = "Limpar metadados";
            runButton.Style = ButtonStyle.Primary;
            runButton.Enabled = false;
            runButton.Click += OnRun;
            Controls.Add(runButton);

            log = new RichTextBox();
            log.BackColor = Theme.LogBg;
            log.ForeColor = Theme.TextSecondary;
            log.BorderStyle = BorderStyle.None;
            log.ReadOnly = true;
            log.Font = Theme.Mono;
            log.DetectUrls = false;
            log.HandleCreated += delegate { Native.DarkScrollbars(log); };
            Controls.Add(log);

            footer = new Label();
            footer.Font = Theme.Caption;
            footer.ForeColor = Theme.TextTertiary;
            footer.AutoSize = false;
            footer.AutoEllipsis = true;
            footer.Text = "";
            Controls.Add(footer);
        }

        // Qualquer superficie grande aceita arquivos soltos, nao so a area
        // pontilhada: quem ja tem uma fila solta o proximo em cima dela.
        void EnableDrop(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += OnDragEnter;
            c.DragDrop += OnDragDrop;
        }

        Label MakeCaption(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.CaptionStrong;
            l.ForeColor = Theme.TextSecondary;
            l.AutoSize = false;
            l.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(l);
            return l;
        }

        // ------------------------------------------------------------
        // Layout manual (todas as medidas em px de 96 dpi, escaladas por P)
        // ------------------------------------------------------------
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        void Relayout()
        {
            if (titleLabel == null) return;
            SuspendLayout();

            int m = P(28);
            int w = ClientSize.Width - m * 2;
            int y = P(22);

            titleLabel.SetBounds(m, y, w, P(30));
            y += P(30);
            subtitleLabel.SetBounds(m, y, w, P(22));
            y += P(22) + P(18);

            if (depPanel.Visible)
            {
                depPanel.SetBounds(m, y, w, P(48));
                depButton.SetBounds(w - P(12) - P(170), P(9), P(170), P(30));
                depLabel.SetBounds(P(14), 0, w - P(14) - P(190), P(48));
                y += P(48) + P(16);
            }

            // de baixo para cima
            int bottom = ClientSize.Height - P(16);
            footer.SetBounds(m, bottom - P(18), w, P(18));
            bottom -= P(18) + P(10);

            if (detailsOpen)
            {
                log.SetBounds(m, bottom - P(190), w, P(190));
                bottom -= P(190) + P(12);
            }
            else
            {
                // Altura zero em vez de Visible=false: o RichTextBox escondido
                // perde a posicao de rolagem e reaparece com o texto no lugar errado.
                log.SetBounds(m, bottom, w, 0);
            }

            // linha de acao: [progresso/status ..................] [Detalhes] [Abrir pasta] [Limpar]
            int bh = P(34);
            int runW = P(170), folderW = P(128), detW = P(100);
            int ax = m + w;
            runButton.SetBounds(ax - runW, bottom - bh, runW, bh); ax -= runW + P(8);
            folderButton.SetBounds(ax - folderW, bottom - bh, folderW, bh); ax -= folderW + P(8);
            detailsButton.SetBounds(ax - detW, bottom - bh, detW, bh); ax -= detW + P(16);
            progress.SetBounds(m, bottom - bh + P(6), ax - m, P(8));
            statusLabel.SetBounds(m, bottom - bh + P(14), ax - m, P(20));
            bottom -= bh + P(16);

            // linha de opcoes: so o que a fila pede
            bool anyVideo = false, anyImage = false;
            foreach (FileRow r in list.Rows) { if (r.IsImage) anyImage = true; else anyVideo = true; }
            videoLabel.Visible = videoQuality.Visible = anyVideo;
            imageLabel.Visible = imageMode.Visible = anyImage;
            if (anyVideo || anyImage)
            {
                int oh = P(32);
                int ox = m;
                int segW = P(250), lblW = P(58);
                if (anyVideo)
                {
                    videoLabel.SetBounds(ox, bottom - oh, lblW, oh); ox += lblW;
                    videoQuality.SetBounds(ox, bottom - oh, segW, oh); ox += segW + P(28);
                }
                if (anyImage)
                {
                    int segW2 = P(350);
                    if (ox + lblW + segW2 > m + w) { segW2 = m + w - ox - lblW; }
                    imageLabel.SetBounds(ox, bottom - oh, lblW, oh); ox += lblW;
                    imageMode.SetBounds(ox, bottom - oh, Math.Max(P(200), segW2), oh);
                }
                bottom -= oh + P(16);
            }

            // area central
            int top = y;
            if (list.Rows.Count == 0)
            {
                dropZone.Compact = false;
                dropZone.SetBounds(m, top, w, Math.Max(P(120), bottom - top));
                listCard.Visible = false;
                clearButton.Visible = false;
            }
            else
            {
                dropZone.Compact = true;
                dropZone.SetBounds(m, top, w, P(64));
                top += P(64) + P(12);
                listCard.Visible = true;
                listCard.SetBounds(m, top, w, Math.Max(P(80), bottom - top));
                list.SetBounds(P(2), P(4), listCard.Width - P(4), listCard.Height - P(8));
                list.Relayout();
                clearButton.Visible = !running;
                clearButton.SetBounds(w - P(16) - P(110), (P(64) - P(28)) / 2, P(110), P(28));
            }
            dropZone.Invalidate();

            ResumeLayout();
        }

        // ------------------------------------------------------------
        // Runtime embutido / dependencias
        // ------------------------------------------------------------
        void OnShown(object sender, EventArgs e)
        {
            Shown -= OnShown;

            if (!Embedded.Available)
            {
                CheckDependencies();
                return;
            }

            preparing = true;
            SetStatus("Preparando componentes na primeira execução...", Theme.TextSecondary);
            progress.Marquee = true;
            UpdateButtons();

            Thread t = new Thread(delegate()
            {
                string error = null;
                try
                {
                    Embedded.Ensure(delegate(int done, int total)
                    {
                        Post(delegate { statusLabel.Text = "Preparando componentes... " + (done * 100 / total) + "%"; });
                    });
                }
                catch (Exception ex) { error = ex.Message; }

                string err = error;
                Post(delegate
                {
                    preparing = false;
                    progress.Marquee = false;
                    progress.Value = 0;
                    SetStatus("Pronto.", Theme.TextSecondary);
                    CheckDependencies();
                    if (err != null && ffmpegPath == null)
                        AppendLog("Falha ao preparar os componentes embutidos: " + err, LogKind.Err);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        void CheckDependencies()
        {
            ffmpegPath = Tools.Find("ffmpeg.exe");
            exiftoolPath = Tools.Find("exiftool.exe");

            if (ffmpegPath != null && exiftoolPath != null)
            {
                bool builtIn = ffmpegPath.StartsWith(Embedded.Dir, StringComparison.OrdinalIgnoreCase);
                footer.Text = "ffmpeg e exiftool " + (builtIn ? "embutidos no programa" : "instalados no sistema")
                    + "   ·   a saída vai para a mesma pasta do original, com nome aleatório; o original não é tocado";
                depPanel.Visible = false;
            }
            else
            {
                List<string> missing = new List<string>();
                if (ffmpegPath == null) missing.Add("ffmpeg");
                if (exiftoolPath == null) missing.Add("exiftool");
                depLabel.Text = "Faltando: " + string.Join(" e ", missing.ToArray())
                    + ". Instale, ou coloque o .exe na pasta deste programa.";
                depPanel.Visible = true;
                footer.Text = "";
            }
            Relayout();
            UpdateButtons();

            // SEMRASTRO_AUTORUN=1 dispara a limpeza assim que estiver pronto
            // (para testes automatizados e screenshots).
            if (Environment.GetEnvironmentVariable("SEMRASTRO_AUTORUN") == "1" && runButton.Enabled && !running)
                OnRun(this, EventArgs.Empty);
        }

        void OnInstallDeps(object sender, EventArgs e)
        {
            string msg = "Será usado o winget (gerenciador de pacotes do Windows) para instalar:\r\n\r\n";
            if (ffmpegPath == null) msg += "   • Gyan.FFmpeg\r\n";
            if (exiftoolPath == null) msg += "   • OliverBetz.ExifTool\r\n";
            msg += "\r\nUma janela de terminal será aberta. Continuar?";

            if (MessageBox.Show(this, msg, "Instalar dependências",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            List<string> cmds = new List<string>();
            if (ffmpegPath == null)
                cmds.Add("winget install --id Gyan.FFmpeg -e --accept-package-agreements --accept-source-agreements");
            if (exiftoolPath == null)
                cmds.Add("winget install --id OliverBetz.ExifTool -e --accept-package-agreements --accept-source-agreements");
            cmds.Add("echo.");
            cmds.Add("echo Feche esta janela para voltar ao programa.");
            cmds.Add("pause");

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                    "/c " + string.Join(" & ", cmds.ToArray()));
                psi.UseShellExecute = true;
                Process p = Process.Start(psi);
                depButton.Enabled = false;
                depButton.Text = "Instalando...";
                ThreadPool.QueueUserWorkItem(delegate(object state)
                {
                    try { p.WaitForExit(); }
                    catch { }
                    Post(delegate
                    {
                        depButton.Enabled = true;
                        depButton.Text = "Instalar com winget";
                        CheckDependencies();
                    });
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Não foi possível iniciar o winget:\r\n" + ex.Message,
                    "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ------------------------------------------------------------
        // Fila de arquivos
        // ------------------------------------------------------------
        void OnPickFiles(object sender, EventArgs e)
        {
            if (running) return;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Selecione vídeos ou imagens";
                dlg.Multiselect = true;
                dlg.Filter =
                    "Vídeos e imagens|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.wmv;*.flv;*.ts;*.mpg;*.mpeg;*.3gp;*.m2ts;"
                        + "*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.tif;*.tiff;*.heic;*.heif;*.avif;*.gif"
                    + "|Vídeos|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.wmv;*.flv;*.ts;*.mpg;*.mpeg;*.3gp;*.m2ts"
                    + "|Imagens|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.tif;*.tiff;*.heic;*.heif;*.avif;*.gif"
                    + "|Todos os arquivos|*.*";
                if (dlg.ShowDialog(this) == DialogResult.OK) AddFiles(dlg.FileNames);
            }
        }

        void OnDragEnter(object sender, DragEventArgs e)
        {
            if (running) { e.Effect = DragDropEffects.None; return; }
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
        }

        void OnDragDrop(object sender, DragEventArgs e)
        {
            if (running) return;
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0) AddFiles(files);
        }

        void AddFiles(IEnumerable<string> paths)
        {
            int added = 0, ignored = 0;
            foreach (string raw in paths)
            {
                string path = raw;
                try { path = Path.GetFullPath(raw); }
                catch { }

                if (Directory.Exists(path))
                {
                    // pasta inteira: pega o que for suportado (recursivo)
                    foreach (string f in EnumerateSupported(path))
                        if (AddOne(f)) added++;
                    continue;
                }
                if (!File.Exists(path)) continue;
                if (!Kinds.IsSupported(path))
                {
                    ignored++;
                    continue;
                }
                if (AddOne(path)) added++;
            }

            if (ignored > 0 && added == 0)
                SetStatus(ignored == 1 ? "Esse arquivo não é um vídeo nem uma imagem suportada."
                    : ignored + " arquivos ignorados: formato não suportado.", Theme.Warn);
            else if (ignored > 0)
                SetStatus(ignored + " arquivo(s) ignorado(s) por formato não suportado.", Theme.Warn);
            else if (added > 0)
                SetStatus("Pronto.", Theme.TextSecondary);

            AfterQueueChanged();
        }

        static IEnumerable<string> EnumerateSupported(string dir)
        {
            List<string> found = new List<string>();
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                    if (Kinds.IsSupported(f)) found.Add(f);
                foreach (string d in Directory.GetDirectories(dir))
                    found.AddRange(EnumerateSupported(d));
            }
            catch { }
            return found;
        }

        bool AddOne(string path)
        {
            foreach (FileRow r in list.Rows)
                if (string.Equals(r.FilePath, path, StringComparison.OrdinalIgnoreCase)) return false;

            FileRow row = new FileRow();
            row.FilePath = path;
            row.IsImage = Kinds.IsImage(path);
            double mb = 0;
            try { mb = new FileInfo(path).Length / 1024.0 / 1024.0; }
            catch { }
            string kind = row.IsImage
                ? (Kinds.IsLosslessOnly(path) ? "imagem (só sem perda)" : "imagem")
                : "vídeo";
            row.Meta = kind + "   ·   " + (mb < 10 ? mb.ToString("0.0") : mb.ToString("0")) + " MB   ·   " + Path.GetDirectoryName(path);
            row.RemoveClicked += delegate { if (!running) { list.Remove(row); AfterQueueChanged(); } };
            row.Text = row.AccessibleName = Path.GetFileName(path);   // Text = nome da janela: e o que a acessibilidade le
            EnableDrop(row);
            list.Add(row);
            return true;
        }

        void AfterQueueChanged()
        {
            outputs.Clear();
            folderButton.Enabled = false;
            Relayout();
            UpdateButtons();
        }

        void UpdateButtons()
        {
            int pending = 0;
            foreach (FileRow r in list.Rows) if (r.State != RowState.Done) pending++;

            if (running)
            {
                runButton.Text = "Cancelar";
                runButton.Style = ButtonStyle.Danger;
                runButton.Enabled = true;
            }
            else
            {
                runButton.Style = ButtonStyle.Primary;
                runButton.Text = pending <= 1 ? "Limpar metadados" : "Limpar " + pending + " arquivos";
                runButton.Enabled = !preparing && pending > 0 && ffmpegPath != null;
            }
            runButton.Invalidate();
            dropZone.Cursor = running ? Cursors.Default : Cursors.Hand;
            videoQuality.Enabled = imageMode.Enabled = !running;
            clearButton.Visible = !running && list.Rows.Count > 0;
            foreach (FileRow r in list.Rows) { r.CanRemove = !running; r.Invalidate(); }
        }

        // ------------------------------------------------------------
        // Execucao em lote
        // ------------------------------------------------------------
        void OnRun(object sender, EventArgs e)
        {
            if (running)
            {
                // A flag do lote cobre a janela entre um arquivo e o proximo,
                // quando Cleaner.Run ainda nao comecou e resetaria Cancelled.
                cancelRequested = true;
                if (cleaner != null) cleaner.Cancel();
                return;
            }
            if (ffmpegPath == null) return;

            List<FileRow> queue = new List<FileRow>();
            foreach (FileRow r in list.Rows) if (r.State != RowState.Done) queue.Add(r);
            if (queue.Count == 0) return;

            bool needsExiftool = false;
            foreach (FileRow r in queue)
                if (r.IsImage && (imageMode.SelectedIndex == 2 || Kinds.IsLosslessOnly(r.FilePath))) needsExiftool = true;

            if (exiftoolPath == null)
            {
                string msg = needsExiftool
                    ? "O exiftool não foi encontrado. Imagens no modo 'Sem perda' (e HEIC, AVIF, GIF) "
                      + "não podem ser limpas sem ele e vão falhar.\r\n\r\nContinuar mesmo assim?"
                    : "O exiftool não foi encontrado.\r\n\r\nA reencodificação pelo ffmpeg será feita "
                      + "normalmente, mas a limpeza extra e a auditoria de metadados serão puladas.\r\n\r\nContinuar assim?";
                if (MessageBox.Show(this, msg, "exiftool ausente", MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning) != DialogResult.OK) return;
            }

            running = true;
            cancelRequested = false;
            outputs.Clear();
            folderButton.Enabled = false;
            log.Clear();
            progress.Value = 0;
            UpdateButtons();
            Relayout();

            int vIdx = videoQuality.SelectedIndex;
            int iIdx = imageMode.SelectedIndex;

            cleaner = new Cleaner();
            cleaner.FfmpegPath = ffmpegPath;
            cleaner.ExiftoolPath = exiftoolPath;
            cleaner.Log = AppendLog;

            worker = new Thread(delegate() { RunBatch(queue, vIdx, iIdx); });
            worker.IsBackground = true;
            worker.Start();
        }

        void RunBatch(List<FileRow> queue, int vIdx, int iIdx)
        {
            int n = queue.Count;
            int ok = 0, fail = 0;
            bool cancelled = false;

            for (int i = 0; i < n; i++)
            {
                FileRow row = queue[i];
                int index = i;
                string name = Path.GetFileName(row.FilePath);

                if (cancelRequested)
                {
                    cancelled = true;
                    Post(delegate { row.State = RowState.Cancelled; row.Invalidate(); });
                    break;
                }

                Post(delegate
                {
                    row.State = RowState.Running; row.Percent = 0; row.Invalidate();
                    list.ScrollIntoView(row);
                });

                cleaner.Status = delegate(string s)
                {
                    Post(delegate { SetStatus((n > 1 ? "[" + (index + 1) + "/" + n + "] " : "") + name + "  ·  " + s, Theme.TextSecondary); });
                };
                cleaner.Progress = delegate(int pct)
                {
                    Post(delegate
                    {
                        row.Percent = pct; row.Invalidate();
                        progress.Value = (int)((index + pct / 100.0) * 100.0 / n);
                    });
                };

                if (n > 1)
                {
                    AppendLog("", LogKind.Text);
                    AppendLog("══════════  " + (i + 1) + "/" + n + "  ·  " + name + "  ══════════", LogKind.Accent);
                }

                string outPath = cleaner.Run(row.FilePath, vIdx, iIdx);

                if (cleaner.Cancelled || cancelRequested)
                {
                    cancelled = true;
                    Post(delegate { row.State = RowState.Cancelled; row.Detail = ""; row.Invalidate(); });
                    break;
                }
                if (outPath != null)
                {
                    ok++;
                    lock (outputs) outputs.Add(outPath);
                    Post(delegate { row.State = RowState.Done; row.Detail = Path.GetFileName(outPath); row.Percent = 100; row.Invalidate(); });
                }
                else
                {
                    fail++;
                    Post(delegate { row.State = RowState.Error; row.Detail = "falhou - veja os detalhes"; row.Invalidate(); });
                }
            }

            int okF = ok, failF = fail; bool cancF = cancelled;
            Post(delegate
            {
                running = false;
                worker = null;
                foreach (FileRow r in queue)
                    if (r.State == RowState.Running) { r.State = RowState.Pending; r.Invalidate(); }
                progress.Value = cancF ? 0 : 100;

                string summary;
                Color color;
                if (cancF)
                {
                    summary = "Cancelado." + (okF > 0 ? "  " + okF + " arquivo(s) já limpos foram mantidos." : "");
                    color = Theme.Warn;
                }
                else if (failF == 0)
                {
                    summary = okF == 1 ? "Concluído. 1 arquivo limpo." : "Concluído. " + okF + " arquivos limpos.";
                    color = Theme.Ok;
                }
                else
                {
                    summary = okF + " limpo(s), " + failF + " com erro. Veja os detalhes.";
                    color = Theme.Err;
                    if (!detailsOpen) { detailsOpen = true; detailsButton.Glyph = "\uE70E"; }
                }
                SetStatus(summary, color);
                folderButton.Enabled = outputs.Count > 0;
                UpdateButtons();
                Relayout();
                if (detailsOpen) Post(delegate { Native.ScrollToBottom(log); });
            });
        }

        // ------------------------------------------------------------
        // Helpers de UI (thread-safe)
        // ------------------------------------------------------------
        void Post(MethodInvoker action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(action); }
            catch { }
        }

        void SetStatus(string text, Color color)
        {
            statusLabel.Text = text;
            statusLabel.ForeColor = color;
        }

        void AppendLog(string text, LogKind kind)
        {
            Post(delegate
            {
                Color color;
                switch (kind)
                {
                    case LogKind.Muted: color = Theme.TextTertiary; break;
                    case LogKind.Accent: color = Theme.Accent; break;
                    case LogKind.Ok: color = Theme.Ok; break;
                    case LogKind.Warn: color = Theme.Warn; break;
                    case LogKind.Err: color = Theme.Err; break;
                    default: color = Theme.Text; break;
                }
                log.SelectionStart = log.TextLength;
                log.SelectionLength = 0;
                log.SelectionColor = color;
                log.AppendText(text + "\r\n");
                log.SelectionColor = Theme.Text;
                if (detailsOpen) Native.ScrollToBottom(log);
            });
        }

        void OnOpenFolder(object sender, EventArgs e)
        {
            string first = null;
            lock (outputs) if (outputs.Count > 0) first = outputs[0];
            if (first == null || !File.Exists(first)) return;
            try { Process.Start("explorer.exe", "/select,\"" + first + "\""); }
            catch { }
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.O) { OnPickFiles(this, EventArgs.Empty); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape && running) { OnRun(this, EventArgs.Empty); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter && !running && runButton.Enabled) { OnRun(this, EventArgs.Empty); e.Handled = true; }
        }

        void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!running) return;
            if (MessageBox.Show(this, "Uma limpeza está em andamento. Cancelar e sair?",
                "Sair", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            cancelRequested = true;
            if (cleaner != null) cleaner.Cancel();
        }
    }
}
