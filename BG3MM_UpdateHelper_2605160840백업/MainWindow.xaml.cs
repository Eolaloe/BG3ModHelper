using System.Windows;
using BG3MM_UpdateHelper.ViewModels;

namespace BG3MM_UpdateHelper;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel(this);
    }
}
