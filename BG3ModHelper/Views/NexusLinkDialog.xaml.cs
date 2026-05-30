using System.Windows;
using BG3ModHelper.ViewModels;

namespace BG3ModHelper.Views;

public partial class NexusLinkDialog : Window
{
    public NexusLinkDialog(NexusLinkDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        vm.CloseRequested += result =>
        {
            DialogResult = result;
            Close();
        };

        Loaded += (_, _) => UrlBox.Focus();
    }
}
