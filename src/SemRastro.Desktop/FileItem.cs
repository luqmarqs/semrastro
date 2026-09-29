using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Media;

namespace SemRastro.Desktop
{
    public enum RowState { Pending, Running, Done, Error, Cancelled }

    // Uma linha da fila (INotifyPropertyChanged para a lista atualizar sozinha).
    public class FileItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public string Path { get; private set; }
        public string Name { get; private set; }
        public bool IsImage { get; private set; }
        public bool IsVideo { get { return !IsImage; } }
        public string Meta { get; private set; }

        RowState state = RowState.Pending;
        int percent;
        string detail = "";
        string output;

        public FileItem(string path)
        {
            Path = path;
            Name = System.IO.Path.GetFileName(path);
            IsImage = Kinds.IsImage(path);
            double mb = 0;
            try { mb = new FileInfo(path).Length / 1024.0 / 1024.0; } catch { }
            string kind = IsImage ? (Kinds.IsLosslessOnly(path) ? "imagem (só sem perda)" : "imagem") : "vídeo";
            Meta = kind + "   ·   " + (mb < 10 ? mb.ToString("0.0") : mb.ToString("0")) + " MB   ·   "
                + System.IO.Path.GetDirectoryName(path);
        }

        public RowState State
        {
            get { return state; }
            set { state = value; Raise("State"); Raise("StatusText"); Raise("StatusBrush"); Raise("IsRunning"); Raise("SubText"); Raise("SubBrush"); Raise("CanRemove"); }
        }

        public int Percent
        {
            get { return percent; }
            set { percent = value; Raise("Percent"); Raise("StatusText"); }
        }

        public string Detail
        {
            get { return detail; }
            set { detail = value ?? ""; Raise("Detail"); Raise("SubText"); }
        }

        public string Output
        {
            get { return output; }
            set { output = value; Raise("Output"); Raise("SubText"); }
        }

        public bool CanRemoveFlag = true;
        public bool CanRemove { get { return CanRemoveFlag; } }
        public bool IsRunning { get { return state == RowState.Running; } }

        public string StatusText
        {
            get
            {
                switch (state)
                {
                    case RowState.Running: return percent + "%";
                    case RowState.Done: return "✓  Limpo";
                    case RowState.Error: return "✕  Erro";
                    case RowState.Cancelled: return "Cancelado";
                    default: return "Na fila";
                }
            }
        }

        public string SubText
        {
            get
            {
                if (state == RowState.Done && output != null)
                    return "salvo como " + System.IO.Path.GetFileName(output) + "   ·   na mesma pasta";
                if ((state == RowState.Error || state == RowState.Cancelled) && detail.Length > 0)
                    return detail;
                return Meta;
            }
        }

        public IBrush StatusBrush
        {
            get
            {
                switch (state)
                {
                    case RowState.Done: return Palette.Ok;
                    case RowState.Error: return Palette.Err;
                    case RowState.Running: return Palette.Text;
                    default: return Palette.Muted;
                }
            }
        }

        public IBrush SubBrush { get { return state == RowState.Error ? Palette.Err : Palette.Muted; } }

        public void RefreshCanRemove() { Raise("CanRemove"); }

        void Raise([CallerMemberName] string name = null)
        {
            PropertyChangedEventHandler h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }

    // Cores semanticas que funcionam nos dois temas (atualizadas pela janela
    // conforme o tema real).
    public static class Palette
    {
        public static IBrush Ok = new SolidColorBrush(Color.Parse("#0F7B0F"));
        public static IBrush Err = new SolidColorBrush(Color.Parse("#C42B1C"));
        public static IBrush Warn = new SolidColorBrush(Color.Parse("#9D5D00"));
        public static IBrush Text = new SolidColorBrush(Color.Parse("#1B1B1B"));
        public static IBrush Muted = new SolidColorBrush(Color.Parse("#7A7A7A"));

        public static void Apply(bool dark)
        {
            Ok = new SolidColorBrush(Color.Parse(dark ? "#6CCB5F" : "#0F7B0F"));
            Err = new SolidColorBrush(Color.Parse(dark ? "#FF99A4" : "#C42B1C"));
            Warn = new SolidColorBrush(Color.Parse(dark ? "#FCE100" : "#9D5D00"));
            Text = new SolidColorBrush(Color.Parse(dark ? "#FFFFFF" : "#1B1B1B"));
            Muted = new SolidColorBrush(Color.Parse(dark ? "#9A9A9A" : "#7A7A7A"));
        }
    }
}
