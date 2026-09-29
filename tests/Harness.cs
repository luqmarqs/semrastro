using System;
using System.IO;

namespace SemRastro
{
    // Roda o pipeline real do app (classe Cleaner) sem abrir a janela.
    // Uso: Harness.exe <entrada> <modo>
    //   modo (video):  0 = CRF 14, 1 = CRF 18, 2 = CRF 23
    //   modo (imagem): 0 = recodificar maxima, 1 = recodificar alta, 2 = sem perda
    // Imprime o caminho da saida, ou "FAIL".
    static class Harness
    {
        [STAThread]
        static int Main(string[] args)
        {
            string input = Path.GetFullPath(args[0]);
            int idx = int.Parse(args[1]);

            Cleaner c = new Cleaner();
            c.FfmpegPath = Tools.Find("ffmpeg.exe");
            c.ExiftoolPath = Tools.Find("exiftool.exe");
            c.Log = delegate(string text, LogKind kind) { Console.Error.WriteLine(text); };

            string outPath = c.Run(input, idx, idx);
            if (outPath == null) { Console.WriteLine("FAIL"); return 1; }
            Console.WriteLine(outPath);
            return 0;
        }
    }
}
