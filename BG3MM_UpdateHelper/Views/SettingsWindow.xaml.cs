using System;
using System.Diagnostics;
using System.Windows;
using BG3MM_UpdateHelper.Models;
using BG3MM_UpdateHelper.Services;
using Microsoft.Win32;

namespace BG3MM_UpdateHelper.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        LoadToUI();
    }

    private void LoadToUI()
    {
        BG3MMPathBox.Text    = _settings.BG3MMFolderPath;
        ModsPathBox.Text     = !string.IsNullOrEmpty(_settings.ModsFolderPath)
            ? _settings.ModsFolderPath
            : PathDiscovery.GetDefaultModsFolder();
        NexusKeyBox.Text     = _settings.NexusAPIKey;
        ModioKeyBox.Text     = _settings.ModioAPIKey;
        BackupCheckBox.IsChecked = _settings.BackupBeforeUpdate;
        DataFolderText.Text  = SettingsStore.GetDataFolder();
        DataFolderLink.NavigateUri    = new Uri(SettingsStore.GetDataFolder());
        ApiKeyFolderText.Text        = System.IO.Path.Combine(SettingsStore.GetDataFolder(), "settings.json");
        ApiKeyFolderLink.NavigateUri = new Uri(System.IO.Path.Combine(SettingsStore.GetDataFolder(), "settings.json"));
    }

    private void BrowseBG3MM_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = $"Select the folder containing {Constants.BG3MM_EXE_NAME}",
            InitialDirectory = string.IsNullOrEmpty(BG3MMPathBox.Text)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : BG3MMPathBox.Text
        };

        if (dialog.ShowDialog() == true)
            BG3MMPathBox.Text = dialog.FolderName;
    }

    private void BrowseMods_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the BG3 mods folder",
            InitialDirectory = string.IsNullOrEmpty(ModsPathBox.Text)
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : ModsPathBox.Text
        };

        if (dialog.ShowDialog() == true)
            ModsPathBox.Text = dialog.FolderName;
    }

    private void OpenNexusKeyPage_Click(object sender, RoutedEventArgs e) =>
        OpenUrl(Constants.NEXUS_API_KEY_HELP_URL);

    private void OpenModioKeyPage_Click(object sender, RoutedEventArgs e) =>
        OpenUrl(Constants.MODIO_API_KEY_HELP_URL);

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open URL: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DataFolder_RequestNavigate(object sender,
        System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", SettingsStore.GetDataFolder()); }
        catch { /* ignored */ }
        e.Handled = true;
    }

    private void ApiKeyFile_RequestNavigate(object sender,
        System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            var path = System.IO.Path.Combine(SettingsStore.GetDataFolder(), "settings.json");
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch { /* ignored */ }
        e.Handled = true;
    }

    private void Hyperlink_RequestNavigate(object sender,
        System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.BG3MMFolderPath = BG3MMPathBox.Text.Trim();

        // Clear the mods path if it matches the default (let auto-discovery handle it)
        var modsPath = ModsPathBox.Text.Trim();
        _settings.ModsFolderPath = modsPath == PathDiscovery.GetDefaultModsFolder() ? "" : modsPath;

        _settings.NexusAPIKey        = NexusKeyBox.Text.Trim();
        _settings.ModioAPIKey        = ModioKeyBox.Text.Trim();
        _settings.BackupBeforeUpdate = BackupCheckBox.IsChecked ?? false;


        SettingsStore.Save(_settings);
        Logger.Info("Settings saved");

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
