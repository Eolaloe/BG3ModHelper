using System.Windows;
using BG3MM_UpdateHelper.ViewModels;

namespace BG3MM_UpdateHelper.Views;

public partial class UpdateNotificationWindow : Window
{
    private readonly UpdateNotificationViewModel _vm;

    public UpdateNotificationWindow(UpdateNotificationViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.CloseRequested += Close;
    }
}
