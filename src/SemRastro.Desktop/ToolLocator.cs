using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace SemRastro.Desktop
{
    // Localiza ffmpeg, exiftool e perl a partir do PACOTE instalado, nunca da
    // pasta do codigo-fonte nem do diretorio atual:
    //
    //   macOS  (SemRastro.app):  Contents/MacOS/SemRastro  ->  Contents/Resources/bin/ffmpeg
    //                                                           Contents/Resources/exiftool/exiftool
    //   Linux  (AppImage):       $APPDIR/usr/lib/semrastro/SemRastro -> $APPDIR/usr/bin/ffmpeg
    //                                                                    $APPDIR/usr/share/semrastro/exiftool/exiftool
    //   Qualquer:                pasta do executavel, subpastas bin/ e exiftool/
    //   Override:                SEMRASTRO_TOOLS=<pasta> (testes)
    //   Ultimo recurso:          PATH do sistema (documentado como fallback, nao como requisito)
    static class ToolLocator
    {
        public static string Ffmpeg, Exiftool, Perl, Source = "";

        static bool Win { get { return RuntimeInformation.IsOSPlatform(OSPlatform.Windows); } }

        public static void Resolve()
        {
            string ff = Win ? "ffmpeg.exe" : "ffmpeg";
            string et = Win ? "exiftool.exe" : "exiftool";

            foreach (string dir in CandidateDirs())
            {
                if (Ffmpeg == null)
                {
                    string p = Path.Combine(dir, ff);
                    if (File.Exists(p)) { Ffmpeg = p; Source = dir; }
                    p = Path.Combine(dir, "bin", ff);
                    if (Ffmpeg == null && File.Exists(p)) { Ffmpeg = p; Source = dir; }
                }
                if (Exiftool == null)
                {
                    string p = Path.Combine(dir, "exiftool", et);
                    if (File.Exists(p)) Exiftool = p;
                    p = Path.Combine(dir, et);
                    if (Exiftool == null && File.Exists(p) && !Directory.Exists(p)) Exiftool = p;
                }
                if (Ffmpeg != null && Exiftool != null) break;
            }

            if (Ffmpeg == null) Ffmpeg = FindOnPath(ff);
            if (Exiftool == null) Exiftool = FindOnPath(et);

            // exiftool e um script Perl fora do Windows
            if (!Win && Exiftool != null)
            {
                foreach (string p in new string[] { "/usr/bin/perl", "/usr/local/bin/perl", "/opt/homebrew/bin/perl", "/opt/local/bin/perl" })
                    if (File.Exists(p)) { Perl = p; break; }
                if (Perl == null) Perl = FindOnPath("perl");
                if (Perl == null) Exiftool = null;   // sem perl nao ha exiftool
            }
        }

        static IEnumerable<string> CandidateDirs()
        {
            string over = Environment.GetEnvironmentVariable("SEMRASTRO_TOOLS");
            if (!string.IsNullOrEmpty(over)) yield return over;

            string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            yield return baseDir;
            yield return Path.Combine(baseDir, "tools");

            // macOS: Contents/MacOS -> Contents/Resources
            string contents = Path.GetDirectoryName(baseDir);
            if (contents != null)
            {
                yield return Path.Combine(contents, "Resources");
                yield return Path.Combine(contents, "Resources", "bin");
            }

            // Linux AppImage
            string appDir = Environment.GetEnvironmentVariable("APPDIR");
            if (!string.IsNullOrEmpty(appDir))
            {
                yield return Path.Combine(appDir, "usr", "bin");
                yield return Path.Combine(appDir, "usr", "share", "semrastro");
            }
            // AppDir deduzido do executavel (usr/lib/semrastro/SemRastro -> usr/)
            string usr = Path.GetDirectoryName(Path.GetDirectoryName(baseDir));
            if (usr != null)
            {
                yield return Path.Combine(usr, "bin");
                yield return Path.Combine(usr, "share", "semrastro");
            }

            // Windows (so para desenvolvimento local): runtime extraido pela versao WinForms
            if (Win)
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                yield return Path.Combine(local, "SemRastro", "runtime");
            }
        }

        static string FindOnPath(string exe)
        {
            string path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) return null;
            foreach (string d in path.Split(Path.PathSeparator))
            {
                if (d.Trim().Length == 0) continue;
                try
                {
                    string p = Path.Combine(d.Trim(), exe);
                    if (File.Exists(p)) { if (Source == "") Source = "PATH"; return p; }
                }
                catch { }
            }
            return null;
        }
    }
}
