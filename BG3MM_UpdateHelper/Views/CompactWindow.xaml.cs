using System.IO;
using System.Windows;
using System.Windows.Input;
using BG3MM_UpdateHelper.ViewModels;

namespace BG3MM_UpdateHelper.Views;

public partial class CompactWindow : Window
{
    private readonly MainWindowViewModel _vm;

    private static readonly string[] SupportedExtensions = [".zip", ".7z", ".rar", ".pak"];

    public CompactWindow(MainWindowViewModel vm)
    {
        InitializeComponent();
        _vm         = vm;
        DataContext = vm;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragOverlay.Visibility = Visibility.Collapsed;
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        _vm.SaveCompactPosition(Left, Top);
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        Topmost = true;
    }

    // === Drag & Drop ===

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (GetDroppedFiles(e).Any())
            DragOverlay.Visibility = Visibility.Visible;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (pos.X <= 0 || pos.Y <= 0 || pos.X >= ActualWidth || pos.Y >= ActualHeight)
            DragOverlay.Visibility = Visibility.Collapsed;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedFiles(e).Any() ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        try
        {
            DragOverlay.Visibility = Visibility.Collapsed;

            var files = GetDroppedFiles(e).ToList();
            var infos = new List<Services.ArchiveSourceInfo>();

            foreach (var path in files)
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".pak")
                {
                    var pakInfo = await Task.Run(() => _vm.AnalyzePakFile(path));
                    if (pakInfo == null)
                    {
                        MessageBox.Show(
                            $"{Path.GetFileName(path)}\n\nThis does not appear to be a BG3 mod file.",
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

            foreach (var info in infos)
                _vm.AddToInstallQueue(info);

            await _vm.ProcessInstallQueue();
        }
        catch (Exception ex)
        {
            Services.Logger.Error($"CompactWindow.Window_Drop failed: {ex.Message}");
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
