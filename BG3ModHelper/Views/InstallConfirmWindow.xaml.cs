using System.Windows;
using BG3ModHelper.ViewModels;

namespace BG3ModHelper.Views;

public partial class InstallConfirmWindow : Window
{
    private readonly InstallConfirmViewModel _vm;

    public InstallConfirmWindow(InstallConfirmViewModel vm)
    {
        InitializeComponent();
        _vm         = vm;
        DataContext = vm;
        vm.CloseRequested += Close;
    }

    public bool   Confirmed    => _vm.DialogResult;
    public string FinalSource  => _vm.FinalSource;

    private void NexusButton_Click(object sender, RoutedEventArgs e) => _vm.SelectNexus();
    private void ModioButton_Click(object sender, RoutedEventArgs e) => _vm.SelectModio();
}
