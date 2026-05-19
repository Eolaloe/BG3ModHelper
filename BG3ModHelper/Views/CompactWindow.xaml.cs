using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using BG3ModHelper.ViewModels;

namespace BG3ModHelper.Views;

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

    // === WM_MOVING boundary clamp ===

    private const int  WM_EXITSIZEMOVE       = 0x0232;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOZORDER   = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int  cbSize;
        public RECT rcMonitor;   // full monitor area in physical pixels
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] static extern bool     GetWindowRect(IntPtr hwnd, out RECT rc);
    [DllImport("user32.dll")] static extern IntPtr   MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool     SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_EXITSIZEMOVE) return IntPtr.Zero;

        // GetWindowRect and GetMonitorInfo both use physical pixels — DPI-independent
        GetWindowRect(hwnd, out var wrc);
        int w = wrc.Right  - wrc.Left;
        int h = wrc.Bottom - wrc.Top;

        var hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var mi   = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hMon, ref mi);
        var m = mi.rcWork;

        int clampedLeft = Math.Clamp(wrc.Left, m.Left, m.Right  - w);
        int clampedTop  = Math.Clamp(wrc.Top,  m.Top,  m.Bottom - h);

        if (clampedLeft != wrc.Left || clampedTop != wrc.Top)
            SetWindowPos(hwnd, IntPtr.Zero, clampedLeft, clampedTop, 0, 0,
                         SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        return IntPtr.Zero;
    }

    // ===

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

            if (infos.Count == 0) return;

            foreach (var info in infos)
                _vm.AddToInstallQueue(info);

            _vm.EnqueueInstallBatch();
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
