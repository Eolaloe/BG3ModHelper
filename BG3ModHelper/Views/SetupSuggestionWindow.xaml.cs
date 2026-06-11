using System.Windows;
using BG3ModHelper.Models;
using BG3ModHelper.Services;

namespace BG3ModHelper.Views;

public partial class SetupSuggestionWindow : Window
{
    private readonly AppSettings _settings;

    public SetupSuggestionWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        MaxHeight  = SystemParameters.WorkArea.Height - 20;

        // Only show checkboxes for features that are currently off
        NxmCheckBox.Visibility        = !settings.NxmHandlerEnabled  ? Visibility.Visible : Visibility.Collapsed;
        FolderWatchCheckBox.Visibility = !settings.FolderWatchEnabled ? Visibility.Visible : Visibility.Collapsed;

        // Pre-check both
        NxmCheckBox.IsChecked        = true;
        FolderWatchCheckBox.IsChecked = true;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (NxmCheckBox.Visibility == Visibility.Visible && NxmCheckBox.IsChecked == true)
            NxmHandler.EnableHandler(_settings);

        if (FolderWatchCheckBox.Visibility == Visibility.Visible && FolderWatchCheckBox.IsChecked == true)
            _settings.FolderWatchEnabled = true;

        _settings.HasSeenSetupSuggestion = true;
        SettingsStore.Save(_settings);

        DialogResult = true;
        Close();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        _settings.HasSeenSetupSuggestion = true;
        SettingsStore.Save(_settings);

        DialogResult = false;
        Close();
    }
}
