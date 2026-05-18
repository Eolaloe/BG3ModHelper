using System.Windows;
using BG3MM_UpdateHelper.ViewModels;

namespace BG3MM_UpdateHelper.Views;

public partial class DownloadHistoryWindow : Window
{
    public DownloadHistoryWindow(DownloadHistoryViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += Close;
    }
}
