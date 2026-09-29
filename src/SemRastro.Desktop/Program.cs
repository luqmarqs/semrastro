using System;
using System.IO;
using Avalonia;

namespace SemRastro.Desktop
{
    static class Program
    {
        // Ponto de entrada. Com "--cli" roda o pipeline sem interface (usado pelos
        // testes automatizados e pela verificacao em CI); caso contrario abre a janela.
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length >= 1 && args[0] == "--cli") return Cli.Run(args);
            try
            {
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex)
            {
                // Um app de janela nao tem console: sem isso, um erro na
                // inicializacao morre em silencio. Vai para a pasta de dados do
                // usuario (nunca para dentro do pacote).
                WriteCrashLog(ex);
                throw;
            }
        }

        static void WriteCrashLog(Exception ex)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SemRastro");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"),
                    DateTime.Now.ToString("s") + " SemRastro " + typeof(Program).Assembly.GetName().Version + "\n" + ex + "\n\n");
                Console.Error.WriteLine("SemRastro: erro na inicializacao (veja " + Path.Combine(dir, "crash.log") + ")");
                Console.Error.WriteLine(ex.ToString());
            }
            catch { }
        }

        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
        }
    }

    // Modo de linha de comando:
    //   SemRastro --cli --where                       imprime onde achou ffmpeg/exiftool/perl
    //   SemRastro --cli <arquivo> <modo>              limpa e imprime o caminho da saida (ou FAIL)
    //     modo (video):  0 = CRF 14, 1 = CRF 18, 2 = CRF 23
    //     modo (imagem): 0 = recodificar maxima, 1 = recodificar alta, 2 = sem perda
    static class Cli
    {
        public static int Run(string[] args)
        {
            ToolLocator.Resolve();
            if (args.Length >= 2 && args[1] == "--where")
            {
                Console.WriteLine("ffmpeg=" + (ToolLocator.Ffmpeg ?? ""));
                Console.WriteLine("exiftool=" + (ToolLocator.Exiftool ?? ""));
                Console.WriteLine("perl=" + (ToolLocator.Perl ?? ""));
                Console.WriteLine("source=" + ToolLocator.Source);
                Console.WriteLine("base=" + AppContext.BaseDirectory);
                return ToolLocator.Ffmpeg != null && ToolLocator.Exiftool != null ? 0 : 2;
            }
            if (args.Length < 3)
            {
                Console.Error.WriteLine("uso: SemRastro --cli <arquivo> <modo 0-2>   |   SemRastro --cli --where");
                return 64;
            }
            if (ToolLocator.Ffmpeg == null)
            {
                Console.Error.WriteLine("ffmpeg nao encontrado (base=" + AppContext.BaseDirectory + ")");
                return 2;
            }

            string input = Path.GetFullPath(args[1]);
            int mode = int.Parse(args[2]);

            Cleaner c = new Cleaner();
            c.FfmpegPath = ToolLocator.Ffmpeg;
            c.ExiftoolPath = ToolLocator.Exiftool;
            c.PerlPath = ToolLocator.Perl;
            c.Log = delegate (string text, LogKind kind) { Console.Error.WriteLine(text); };

            string outPath = c.Run(input, mode, mode);
            if (outPath == null) { Console.WriteLine("FAIL"); return 1; }
            Console.WriteLine(outPath);
            return 0;
        }
    }
}
