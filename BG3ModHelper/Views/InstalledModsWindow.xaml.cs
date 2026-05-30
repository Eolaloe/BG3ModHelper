using System.Windows;
using BG3ModHelper.ViewModels;

namespace BG3ModHelper.Views;

public partial class InstalledModsWindow : Window
{
    public InstalledModsWindow(InstalledModsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += Close;
    }
}
