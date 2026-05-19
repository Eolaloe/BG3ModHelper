using System.Windows;
using System.Windows.Controls;
using BG3ModHelper.ViewModels;

namespace BG3ModHelper.Views;

public partial class DownloadHistoryWindow : Window
{
    public DownloadHistoryWindow(DownloadHistoryViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += Close;
    }

    private void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        btn.ContextMenu.PlacementTarget = btn;
        btn.ContextMenu.IsOpen = true;
    }
}
