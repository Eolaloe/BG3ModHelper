using System.Windows;
using BG3MM_UpdateHelper.ViewModels;

namespace BG3MM_UpdateHelper;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainWindowViewModel(this);
        DataContext = _vm;
    }

    public void OpenSettingsOnStartup()
    {
        // Dispatch so the window is fully loaded before opening Settings
        Dispatcher.InvokeAsync(() => _vm.OpenSettingsCommand.Execute(null));
    }
}
