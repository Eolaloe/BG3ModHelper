using System.IO;
using System.Windows;
using BG3ModHelper.Services;
using BG3ModHelper.ViewModels;

namespace BG3ModHelper;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;

    private static readonly string[] SupportedArchiveExtensions = [".zip", ".7z", ".rar"];
    private static readonly string[] SupportedExtensions        = [".zip", ".7z", ".rar", ".pak"];

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainWindowViewModel(this);
        DataContext = _vm;
    }

    public void OpenSettingsOnStartup()
    {
        Dispatcher.InvokeAsync(() => _vm.OpenSettingsCommand.Execute(null));
    }

    // === Drag & Drop ===

    private void Window_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DragOverlay.Visibility = Visibility.Collapsed;
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (GetDroppedFiles(e).Any())
            DragOverlay.Visibility = Visibility.Visible;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        // Only hide when leaving the window entirely
        var pos = e.GetPosition(this);
        if (pos.X <= 0 || pos.Y <= 0 || pos.X >= ActualWidth || pos.Y >= ActualHeight)
            DragOverlay.Visibility = Visibility.Collapsed;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedFiles(e).Any()
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        try
        {
            DragOverlay.Visibility = Visibility.Collapsed;

            var files = GetDroppedFiles(e).ToList();

            // Analyze all files first, then enqueue
            var infos = new List<ArchiveSourceInfo>();
            foreach (var path in files)
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".pak")
                {
                    var pakInfo = await Task.Run(() => _vm.AnalyzePakFile(path));
                    if (pakInfo == null)
                    {
                        MessageBox.Show(
                            $"{System.IO.Path.GetFileName(path)}\n\nThis does not appear to be a BG3 mod file.",
                            "Not a BG3 Mod", MessageBoxButton.OK, MessageBoxImage.Warning);
                        continue;
                    }
                    infos.Add(pakInfo);
                }
                else
                {
                    var info = await Task.Run(() => _vm.AnalyzeDroppedArchive(path));
                    if (info != null) infos.Add(info);
                }
            }

            // Enqueue all, then process once
            foreach (var info in infos)
                _vm.AddToInstallQueue(info);

            await _vm.ProcessInstallQueue();
        }
        catch (Exception ex)
        {
            Logger.Error($"MainWindow.Window_Drop failed: {ex.Message}");
            DragOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private static IEnumerable<string> GetDroppedFiles(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return [];

        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        return files.Where(f =>
            SupportedExtensions.Contains(
                Path.GetExtension(f).ToLowerInvariant()));
    }
}
