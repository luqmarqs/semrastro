using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;

namespace SemRastro.Desktop
{
    public partial class MainWindow : Window
    {
        readonly ObservableCollection<FileItem> items = new ObservableCollection<FileItem>();
        readonly List<string> outputs = new List<string>();
        readonly StringBuilder logText = new StringBuilder();

        volatile bool running;
        volatile bool cancelRequested;
        bool detailsOpen;
        Cleaner cleaner;

        public MainWindow() : this(new string[0]) { }

        public MainWindow(string[] args)
        {
            InitializeComponent();
            List.ItemsSource = items;

            ActualThemeVariantChanged += delegate { ApplyPalette(); };
            ApplyPalette();

            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DragEnterEvent, OnDragOver);
            AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
            AddHandler(DragDrop.DropEvent, OnDrop);
            KeyDown += OnKey;

            ToolLocator.Resolve();
            CheckDependencies();

            if (args != null && args.Length > 0) AddFiles(args);
            Relayout();
        }

        void ApplyPalette()
        {
            Palette.Apply(ActualThemeVariant == ThemeVariant.Dark);
            foreach (FileItem it in items) it.State = it.State;   // repinta cores
        }

        // ------------------------------------------------------------
        // Dependencias (vem dentro do pacote; a barra so aparece se faltar)
        // ------------------------------------------------------------
        void CheckDependencies()
        {
            if (ToolLocator.Ffmpeg != null && ToolLocator.Exiftool != null)
            {
                DepBar.IsVisible = false;
                Footer.Text = "ffmpeg e exiftool embutidos no aplicativo   ·   a saída vai para a mesma pasta do original, com nome aleatório; o original não é tocado";
            }
            else
            {
                List<string> missing = new List<string>();
                if (ToolLocator.Ffmpeg == null) missing.Add("ffmpeg");
                if (ToolLocator.Exiftool == null) missing.Add("exiftool" + (ToolLocator.Perl == null && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? " (perl não encontrado)" : ""));
                DepText.Text = "Faltando: " + string.Join(" e ", missing) + ". O pacote parece incompleto ou foi movido sem a pasta de recursos.";
                DepBar.IsVisible = true;
                Footer.Text = "";
            }
        }

        void OnDepHelp(object sender, RoutedEventArgs e)
        {
            AppendLog("ffmpeg procurado a partir de: " + AppContext.BaseDirectory, LogKind.Muted);
            AppendLog("Reinstale o aplicativo a partir do pacote original (.dmg ou .AppImage).", LogKind.Warn);
            AppendLog("No macOS e no Linux o exiftool precisa do interpretador perl do sistema (/usr/bin/perl).", LogKind.Warn);
            if (!detailsOpen) ToggleDetails();
        }

        // ------------------------------------------------------------
        // Fila
        // ------------------------------------------------------------
        async void OnPickFiles(object sender, PointerReleasedEventArgs e)
        {
            if (running) return;
            if (e.Source is Button) return;
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Selecione vídeos ou imagens",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Vídeos e imagens") { Patterns = Patterns(Kinds.VideoExt, Kinds.ImageExt) },
                    new FilePickerFileType("Vídeos") { Patterns = Patterns(Kinds.VideoExt) },
                    new FilePickerFileType("Imagens") { Patterns = Patterns(Kinds.ImageExt) },
                    FilePickerFileTypes.All
                }
            });
            List<string> paths = new List<string>();
            foreach (IStorageFile f in files)
            {
                string p = f.TryGetLocalPath();
                if (p != null) paths.Add(p);
            }
            if (paths.Count > 0) AddFiles(paths);
        }

        static string[] Patterns(params string[][] groups)
        {
            List<string> p = new List<string>();
            foreach (string[] g in groups)
                foreach (string ext in g) { p.Add("*" + ext); p.Add("*" + ext.ToUpperInvariant()); }
            return p.ToArray();
        }

        void OnDragOver(object sender, DragEventArgs e)
        {
            bool ok = !running && e.Data.Contains(DataFormats.Files);
            e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            SetDropActive(ok);
        }

        void OnDragLeave(object sender, RoutedEventArgs e) { SetDropActive(false); }

        void OnDrop(object sender, DragEventArgs e)
        {
            SetDropActive(false);
            if (running) return;
            var files = e.Data.GetFiles();
            if (files == null) return;
            List<string> paths = new List<string>();
            foreach (IStorageItem it in files)
            {
                string p = it.TryGetLocalPath();
                if (p != null) paths.Add(p);
            }
            AddFiles(paths);
        }

        void SetDropActive(bool active)
        {
            DropOutline.StrokeDashArray = active ? null : new Avalonia.Collections.AvaloniaList<double> { 4, 3 };
            DropOutline.Stroke = active ? (IBrush)this.FindResource("SystemControlHighlightAccentBrush") : (IBrush)this.FindResource("SystemControlForegroundBaseMediumLowBrush");
            DropTitle.Text = active ? "Pode soltar" : "Arraste vídeos ou imagens aqui";
        }

        void AddFiles(IEnumerable<string> paths)
        {
            int added = 0, ignored = 0;
            foreach (string raw in paths)
            {
                string path = raw;
                try { path = Path.GetFullPath(raw); } catch { }
                if (Directory.Exists(path))
                {
                    foreach (string f in EnumerateSupported(path)) if (AddOne(f)) added++;
                    continue;
                }
                if (!File.Exists(path)) continue;
                if (!Kinds.IsSupported(path)) { ignored++; continue; }
                if (AddOne(path)) added++;
            }
            if (ignored > 0 && added == 0)
                SetStatus(ignored == 1 ? "Esse arquivo não é um vídeo nem uma imagem suportada." : ignored + " arquivos ignorados: formato não suportado.", Palette.Warn);
            else if (ignored > 0)
                SetStatus(ignored + " arquivo(s) ignorado(s) por formato não suportado.", Palette.Warn);
            else if (added > 0)
                SetStatus("Pronto.", Palette.Muted);
            AfterQueueChanged();
        }

        static IEnumerable<string> EnumerateSupported(string dir)
        {
            List<string> found = new List<string>();
            try
            {
                foreach (string f in Directory.GetFiles(dir)) if (Kinds.IsSupported(f)) found.Add(f);
                foreach (string d in Directory.GetDirectories(dir)) found.AddRange(EnumerateSupported(d));
            }
            catch { }
            return found;
        }

        bool AddOne(string path)
        {
            foreach (FileItem it in items)
                if (string.Equals(it.Path, path, StringComparison.Ordinal)) return false;
            items.Add(new FileItem(path));
            return true;
        }

        void OnRemoveRow(object sender, RoutedEventArgs e)
        {
            if (running) return;
            Button b = sender as Button;
            FileItem it = b != null ? b.Tag as FileItem : null;
            if (it != null) { items.Remove(it); AfterQueueChanged(); }
        }

        void OnClearList(object sender, RoutedEventArgs e)
        {
            if (running) return;
            items.Clear();
            AfterQueueChanged();
        }

        void AfterQueueChanged()
        {
            outputs.Clear();
            FolderButton.IsEnabled = false;
            Relayout();
            UpdateButtons();
        }

        void Relayout()
        {
            bool any = items.Count > 0;
            ListCard.IsVisible = any;
            ClearButton.IsVisible = any && !running;
            DropZone.Height = any ? 64 : 150;
            DropContent.Orientation = any ? Avalonia.Layout.Orientation.Horizontal : Avalonia.Layout.Orientation.Vertical;
            DropContent.Spacing = any ? 12 : 6;
            DropContent.HorizontalAlignment = any ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Center;
            DropContent.Margin = any ? new Thickness(20, 0, 0, 0) : new Thickness(0);
            DropSub.Text = any ? "ou clique para adicionar mais" : "ou clique para escolher  ·  pode soltar vários de uma vez, ou uma pasta inteira";

            bool anyVideo = false, anyImage = false;
            foreach (FileItem it in items) { if (it.IsImage) anyImage = true; else anyVideo = true; }
            VideoLabel.IsVisible = VideoQuality.IsVisible = anyVideo;
            ImageLabel.IsVisible = ImageMode.IsVisible = anyImage;
        }

        void UpdateButtons()
        {
            int pending = 0;
            foreach (FileItem it in items) if (it.State != RowState.Done) pending++;
            if (running)
            {
                RunButton.Content = "Cancelar";
                RunButton.Classes.Remove("accent");
                RunButton.IsEnabled = true;
            }
            else
            {
                if (!RunButton.Classes.Contains("accent")) RunButton.Classes.Add("accent");
                RunButton.Content = pending <= 1 ? "Limpar metadados" : "Limpar " + pending + " arquivos";
                RunButton.IsEnabled = pending > 0 && ToolLocator.Ffmpeg != null;
            }
            VideoQuality.IsEnabled = ImageMode.IsEnabled = !running;
            ClearButton.IsVisible = !running && items.Count > 0;
            foreach (FileItem it in items) { it.CanRemoveFlag = !running; it.RefreshCanRemove(); }
        }

        // ------------------------------------------------------------
        // Execucao em lote
        // ------------------------------------------------------------
        void OnRun(object sender, RoutedEventArgs e)
        {
            if (running)
            {
                cancelRequested = true;
                if (cleaner != null) cleaner.Cancel();
                return;
            }
            if (ToolLocator.Ffmpeg == null) return;

            List<FileItem> queue = new List<FileItem>();
            foreach (FileItem it in items) if (it.State != RowState.Done) queue.Add(it);
            if (queue.Count == 0) return;

            running = true;
            cancelRequested = false;
            outputs.Clear();
            FolderButton.IsEnabled = false;
            logText.Length = 0;
            Log.Text = "";
            Progress.Value = 0;
            UpdateButtons();
            Relayout();

            int vIdx = VideoQuality.SelectedIndex < 0 ? 1 : VideoQuality.SelectedIndex;
            int iIdx = ImageMode.SelectedIndex < 0 ? 1 : ImageMode.SelectedIndex;

            cleaner = new Cleaner();
            cleaner.FfmpegPath = ToolLocator.Ffmpeg;
            cleaner.ExiftoolPath = ToolLocator.Exiftool;
            cleaner.PerlPath = ToolLocator.Perl;
            cleaner.Log = AppendLog;

            Task.Run(delegate { RunBatch(queue, vIdx, iIdx); });
        }

        void RunBatch(List<FileItem> queue, int vIdx, int iIdx)
        {
            int n = queue.Count, ok = 0, fail = 0;
            bool cancelled = false;

            for (int i = 0; i < n; i++)
            {
                FileItem row = queue[i];
                int index = i;
                if (cancelRequested) { cancelled = true; Post(delegate { row.State = RowState.Cancelled; }); break; }

                Post(delegate { row.State = RowState.Running; row.Percent = 0; });
                cleaner.Status = delegate (string s)
                {
                    Post(delegate { SetStatus((n > 1 ? "[" + (index + 1) + "/" + n + "] " : "") + row.Name + "  ·  " + s, Palette.Muted); });
                };
                cleaner.Progress = delegate (int pct)
                {
                    Post(delegate { row.Percent = pct; Progress.Value = (index + pct / 100.0) * 100.0 / n; });
                };
                if (n > 1) AppendLog("══════════  " + (i + 1) + "/" + n + "  ·  " + row.Name + "  ══════════", LogKind.Accent);

                string outPath = cleaner.Run(row.Path, vIdx, iIdx);

                if (cleaner.Cancelled || cancelRequested)
                {
                    cancelled = true;
                    Post(delegate { row.State = RowState.Cancelled; row.Detail = ""; });
                    break;
                }
                if (outPath != null)
                {
                    ok++;
                    lock (outputs) outputs.Add(outPath);
                    Post(delegate { row.Output = outPath; row.Percent = 100; row.State = RowState.Done; });
                }
                else
                {
                    fail++;
                    Post(delegate { row.Detail = "falhou - veja os detalhes"; row.State = RowState.Error; });
                }
            }

            int okF = ok, failF = fail; bool cancF = cancelled;
            Post(delegate
            {
                running = false;
                foreach (FileItem it in queue) if (it.State == RowState.Running) it.State = RowState.Pending;
                Progress.Value = cancF ? 0 : 100;
                if (cancF) SetStatus("Cancelado." + (okF > 0 ? "  " + okF + " arquivo(s) já limpos foram mantidos." : ""), Palette.Warn);
                else if (failF == 0) SetStatus(okF == 1 ? "Concluído. 1 arquivo limpo." : "Concluído. " + okF + " arquivos limpos.", Palette.Ok);
                else
                {
                    SetStatus(okF + " limpo(s), " + failF + " com erro. Veja os detalhes.", Palette.Err);
                    if (!detailsOpen) ToggleDetails();
                }
                FolderButton.IsEnabled = outputs.Count > 0;
                UpdateButtons();
                Relayout();
            });
        }

        // ------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------
        void Post(Action a) { Dispatcher.UIThread.Post(a); }

        void SetStatus(string text, IBrush brush)
        {
            StatusText.Text = text;
            StatusText.Foreground = brush;
        }

        void AppendLog(string text, LogKind kind)
        {
            Post(delegate
            {
                logText.Append(text).Append('\n');
                if (detailsOpen)
                {
                    Log.Text = logText.ToString();
                    Log.CaretIndex = Log.Text.Length;
                }
            });
        }

        void OnToggleDetails(object sender, RoutedEventArgs e) { ToggleDetails(); }

        void ToggleDetails()
        {
            detailsOpen = !detailsOpen;
            DetailsButton.Content = detailsOpen ? "Detalhes ▴" : "Detalhes ▾";
            Log.IsVisible = detailsOpen;
            if (detailsOpen) { Log.Text = logText.ToString(); Log.CaretIndex = Log.Text.Length; }
        }

        void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            string first = null;
            lock (outputs) if (outputs.Count > 0) first = outputs[0];
            if (first == null || !File.Exists(first)) return;
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    Process.Start(new ProcessStartInfo("open", new string[] { "-R", first }) { UseShellExecute = false });
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + first + "\"") { UseShellExecute = false });
                else
                    Process.Start(new ProcessStartInfo("xdg-open", new string[] { Path.GetDirectoryName(first) }) { UseShellExecute = false });
            }
            catch (Exception ex) { AppendLog("Não foi possível abrir a pasta: " + ex.Message, LogKind.Warn); }
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.O && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
            {
                if (!running) OnPickFiles(this, null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && running) { OnRun(this, null); e.Handled = true; }
            else if (e.Key == Key.Enter && !running && RunButton.IsEnabled) { OnRun(this, null); e.Handled = true; }
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (running)
            {
                cancelRequested = true;
                if (cleaner != null) cleaner.Cancel();
            }
            base.OnClosing(e);
        }
    }
}
