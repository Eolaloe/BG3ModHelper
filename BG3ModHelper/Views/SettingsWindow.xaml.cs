using System;
using System.Diagnostics;
using System.Windows;
using BG3ModHelper.Models;
using BG3ModHelper.Services;
using BG3ModHelper.ViewModels;
using Microsoft.Win32;

namespace BG3ModHelper.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings          _settings;
    private readonly MainWindowViewModel? _vm;
    private          bool                 _suppressNxmEvent;

    public SettingsWindow(AppSettings settings, MainWindowViewModel? vm = null)
    {
        InitializeComponent();
        _settings        = settings;
        _vm              = vm;
        // Auto-detect current nxm handler and add to known list.
        // Never add old Helper paths — IsSelfExe guards against that.
        var currentCmd = NxmHandler.ReadCurrentCommand();
        if (!string.IsNullOrEmpty(currentCmd) && !NxmHandler.IsRegisteredToSelf())
        {
            var currentExe = NxmHandler.ExtractExePath(currentCmd);
            if (!NxmHandler.IsSelfExe(currentExe))
            {
                if (!settings.NxmKnownHandlers.Contains(currentCmd))
                {
                    settings.NxmKnownHandlers.Add(currentCmd);
                    SettingsStore.Save(settings);
                }
                // Pre-set as secondary if nothing configured yet
                if (string.IsNullOrEmpty(settings.NxmPreviousHandler))
                {
                    settings.NxmPreviousHandler = currentCmd;
                    SettingsStore.Save(settings);
                }
            }
        }

        // Clean up any stale Helper entries accumulated from previous versions/paths
        var stale = settings.NxmKnownHandlers
            .Where(cmd => NxmHandler.IsSelfExe(NxmHandler.ExtractExePath(cmd)))
            .ToList();
        if (stale.Count > 0)
        {
            stale.ForEach(cmd => settings.NxmKnownHandlers.Remove(cmd));
            SettingsStore.Save(settings);
        }

        LoadToUI();
        WebViewHelper.LoginStateChanged += OnLoginStateChanged;
        Closed += (_, _) => WebViewHelper.LoginStateChanged -= OnLoginStateChanged;
    }

    private void OnLoginStateChanged(bool loggedIn) =>
        Dispatcher.Invoke(() => NexusLoginBtn.Content = loggedIn ? "Nexus Logout" : "Nexus Login");

    private void LoadToUI()
    {
        BG3MMPathBox.Text    = _settings.BG3MMFolderPath;
        ModsPathBox.Text     = !string.IsNullOrEmpty(_settings.ModsFolderPath)
            ? _settings.ModsFolderPath
            : PathDiscovery.GetDefaultModsFolder();
        NexusKeyBox.Password = _settings.NexusAPIKey;
        ModioKeyBox.Password = _settings.ModioAPIKey;
        BackupCheckBox.IsChecked = _settings.BackupBeforeUpdate;
        DataFolderText.Text  = SettingsStore.GetDataFolder();
        DataFolderLink.NavigateUri    = new Uri(SettingsStore.GetDataFolder());
        ApiKeyFolderText.Text        = System.IO.Path.Combine(SettingsStore.GetDataFolder(), "settings.json");
        ApiKeyFolderLink.NavigateUri = new Uri(System.IO.Path.Combine(SettingsStore.GetDataFolder(), "settings.json"));

        NexusLoginBtn.Content     = WebViewHelper.IsLoggedIn() ? "Nexus Logout" : "Nexus Login";
        NexusTierBadge.Visibility = Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(_settings.NexusAPIKey))
        {
            NexusTierText.Text        = _settings.NexusIsPremium ? "Premium" : "Free";
            NexusTierBadge.Background = new System.Windows.Media.SolidColorBrush(
                _settings.NexusIsPremium
                    ? System.Windows.Media.Color.FromRgb(0xd9, 0x82, 0x00)
                    : System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));
            NexusTierBadge.Visibility = Visibility.Visible;
        }

        FolderWatchCheckBox.IsChecked = _settings.FolderWatchEnabled;
        DeleteSourceCheckBox.IsChecked = _settings.DeleteSourceAfterInstall;
        WatchFolderBox.Text = !string.IsNullOrEmpty(_settings.WatchedDownloadFolder)
            ? _settings.WatchedDownloadFolder
            : BG3ModHelper.Services.FolderWatcherService.GetDefaultDownloadsFolder();
        var ver = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(4) ?? "0.0.0";
        VersionRun.Text = $"{ver}  ({Constants.RELEASE_NAME})";

        // Log level combo — populate from enum, select current setting
        var logLevels = new[] { "Debug", "Info", "Warn", "Error" };
        LogLevelCombo.ItemsSource   = logLevels;
        LogLevelCombo.SelectedItem  = logLevels.FirstOrDefault(
            l => string.Equals(l, _settings.LogMinLevel, StringComparison.OrdinalIgnoreCase))
            ?? "Info";
        LogLevelCombo.SelectionChanged += LogLevelCombo_SelectionChanged;
        UpdateLogLevelHint();

        // nxm handler state — suppress event during initial bind
        _suppressNxmEvent = true;
        NxmEnabledCheckBox.IsChecked = _settings.NxmHandlerEnabled;
        _suppressNxmEvent = false;
        RefreshNxmStatus();
    }

    private void GuideLink_Click(object sender, RoutedEventArgs e)
    {
        var guide = new GuideWindow { Owner = this };
        guide.Show();
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

    private async void NexusLogin_Click(object sender, RoutedEventArgs e)
    {
        if (WebViewHelper.IsLoggedIn())
        {
            await WebViewHelper.LogoutAsync();
        }
        else
        {
            var win = new NexusLoginWindow { Owner = this };
            win.ShowDialog();
        }
    }

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

    private void FolderWatch_Checked(object sender, RoutedEventArgs e)
    {
        if (FolderWatchCheckBox.IsChecked == true)
        {
            var result = MessageBox.Show(
                "When enabled, the app will monitor your download folder\n" +
                "for newly created archive files while running.\n\n" +
                "A popup will appear when a mod archive is detected.\n\n" +
                "Enable folder watching?",
                "Enable Download Folder Watching",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information);

            if (result != MessageBoxResult.OK)
                FolderWatchCheckBox.IsChecked = false;
        }
    }

    private void BrowseWatchFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title            = "Select Watch Folder",
            InitialDirectory = string.IsNullOrEmpty(WatchFolderBox.Text)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : WatchFolderBox.Text
        };
        if (dialog.ShowDialog() == true)
            WatchFolderBox.Text = dialog.FolderName;
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

        _settings.NexusAPIKey        = NexusKeyBox.Visibility == Visibility.Visible
            ? NexusKeyBox.Password.Trim()
            : NexusKeyBoxPlain.Text.Trim();
        _settings.ModioAPIKey        = ModioKeyBox.Visibility == Visibility.Visible
            ? ModioKeyBox.Password.Trim()
            : ModioKeyBoxPlain.Text.Trim();
        _settings.BackupBeforeUpdate = BackupCheckBox.IsChecked ?? false;
        _settings.FolderWatchEnabled      = FolderWatchCheckBox.IsChecked ?? false;
        _settings.DeleteSourceAfterInstall = DeleteSourceCheckBox.IsChecked ?? false;
        _settings.WatchedDownloadFolder   = WatchFolderBox.Text.Trim();
        _settings.LogMinLevel = LogLevelCombo.SelectedItem as string ?? "Info";

        // Apply immediately so the new level takes effect without restart.
        if (Enum.TryParse<LogLevel>(_settings.LogMinLevel, out var logLevel))
            Logger.MinLevel = logLevel;

        SettingsStore.Save(_settings);
        Logger.Info("Settings saved");

        DialogResult = true;
        Close();
    }

    private void LogLevelCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdateLogLevelHint();

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        var logFolder = System.IO.Path.Combine(Services.SettingsStore.GetDataFolder(), "logs");
        var files = System.IO.Directory.Exists(logFolder)
            ? System.IO.Directory.GetFiles(logFolder, "*.log")
            : [];

        if (files.Length == 0)
        {
            MessageBox.Show("No log files found.", "Clear Logs", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"Delete {files.Length} log file(s) in\n{logFolder}?",
            "Clear Logs",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        int deleted = 0;
        foreach (var f in files)
        {
            try { System.IO.File.Delete(f); deleted++; }
            catch { /* skip locked files */ }
        }

        // Today's log will be recreated on the next write — nothing else needed.
        Logger.Info($"Logs cleared: {deleted} file(s) deleted");
        MessageBox.Show($"Deleted {deleted} log file(s).", "Clear Logs", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void UpdateLogLevelHint()
    {
        LogLevelHint.Text = (LogLevelCombo.SelectedItem as string) switch
        {
            "Debug" => "Verbose — logs every lookup and match decision",
            "Info"  => "Normal — recommended for everyday use",
            "Warn"  => "Minimal — warnings and errors only",
            "Error" => "Errors only",
            _       => ""
        };
    }

    private void ToggleNexusKey_Click(object sender, RoutedEventArgs e)
    {
        if (NexusKeyBox.Visibility == Visibility.Visible)
        {
            NexusKeyBoxPlain.Text       = NexusKeyBox.Password;
            NexusKeyBox.Visibility      = Visibility.Collapsed;
            NexusKeyBoxPlain.Visibility = Visibility.Visible;
        }
        else
        {
            NexusKeyBox.Password        = NexusKeyBoxPlain.Text;
            NexusKeyBoxPlain.Visibility = Visibility.Collapsed;
            NexusKeyBox.Visibility      = Visibility.Visible;
        }
    }

    private void ToggleModioKey_Click(object sender, RoutedEventArgs e)
    {
        if (ModioKeyBox.Visibility == Visibility.Visible)
        {
            ModioKeyBoxPlain.Text       = ModioKeyBox.Password;
            ModioKeyBox.Visibility      = Visibility.Collapsed;
            ModioKeyBoxPlain.Visibility = Visibility.Visible;
        }
        else
        {
            ModioKeyBox.Password        = ModioKeyBoxPlain.Text;
            ModioKeyBoxPlain.Visibility = Visibility.Collapsed;
            ModioKeyBox.Visibility      = Visibility.Visible;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // === nxm:// handler ===

    /// <summary>
    /// All selectable handler items: [0] = this app, [1..] = KnownHandlers entries.
    /// The list is rebuilt each time LoadHandlerDropdowns is called.
    /// </summary>
    private readonly List<string> _handlerCommands = [];

    private void NxmEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressNxmEvent) return;

        if (NxmEnabledCheckBox.IsChecked == true)
            NxmHandler.EnableHandler(_settings);
        else
            NxmHandler.DisableHandler(_settings);

        RefreshNxmStatus();
    }

    private void HandlerCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ApplyHandlerButton is null) return;
        ApplyHandlerButton.IsEnabled = HasPendingChanges();
    }

    private void ApplyHandlerButton_Click(object sender, RoutedEventArgs e)
    {
        var primaryIdx   = PrimaryHandlerCombo.SelectedIndex;
        var secondaryIdx = SecondaryHandlerCombo.SelectedIndex;
        if (primaryIdx < 0) return;

        var selfCmd      = NxmHandler.GetSelfCommand();
        var primaryCmd   = primaryIdx == 0 ? "" : _handlerCommands[primaryIdx - 1];
        var secondaryCmd = secondaryIdx <= 0 ? "" : _handlerCommands[secondaryIdx - 1];

        if (primaryCmd == selfCmd)
        {
            // Helper → enable + auto check ON
            _settings.NxmPreviousHandler = secondaryCmd;
            NxmHandler.EnableHandler(_settings);
        }
        else if (!string.IsNullOrEmpty(primaryCmd))
        {
            // Other app → register directly + check OFF (never add Helper itself)
            if (!NxmHandler.IsSelfExe(NxmHandler.ExtractExePath(primaryCmd)) &&
                !_settings.NxmKnownHandlers.Contains(primaryCmd))
                _settings.NxmKnownHandlers.Add(primaryCmd);
            NxmHandler.RegisterCommand(primaryCmd);
            _settings.NxmHandlerEnabled  = false;
            _settings.NxmPreviousHandler = secondaryCmd;
            SettingsStore.Save(_settings);
        }
        else
        {
            // (none) → unregister + check OFF
            NxmHandler.Unregister();
            _settings.NxmHandlerEnabled  = false;
            _settings.NxmPreviousHandler = secondaryCmd;
            SettingsStore.Save(_settings);
        }

        ApplyHandlerButton.IsEnabled = false;
        RefreshNxmStatus();
    }

    private bool HasPendingChanges()
    {
        var primaryIdx   = PrimaryHandlerCombo.SelectedIndex;
        var secondaryIdx = SecondaryHandlerCombo.SelectedIndex;
        if (primaryIdx < 0) return false;

        var selfCmd       = NxmHandler.GetSelfCommand();
        var selectedPri   = primaryIdx == 0 ? "" : _handlerCommands[primaryIdx - 1];
        var selectedSec   = secondaryIdx <= 0 ? "" : _handlerCommands[secondaryIdx - 1];
        var currentPri    = NxmHandler.IsRegisteredToSelf()
            ? selfCmd : (NxmHandler.ReadCurrentCommand() ?? "");

        return selectedPri != currentPri ||
               selectedSec != _settings.NxmPreviousHandler;
    }

    private string selfCmdCache => NxmHandler.GetSelfCommand();

    private void LoadHandlerDropdowns()
    {
        _handlerCommands.Clear();
        var selfCmd = NxmHandler.GetSelfCommand();
        _handlerCommands.Add(selfCmd);

        foreach (var cmd in _settings.NxmKnownHandlers)
        {
            if (cmd == selfCmd) continue;
            var exePath = NxmHandler.ExtractExePath(cmd);
            if (NxmHandler.IsSelfExe(exePath)) continue;           // skip old Helper paths
            if (exePath != null && !System.IO.File.Exists(exePath)) continue;
            _handlerCommands.Add(cmd);
        }

        string DisplayName(string cmd)
        {
            if (cmd == selfCmd) return "Helper";
            var exe = NxmHandler.ExtractExePath(cmd);
            if (!string.IsNullOrEmpty(exe))
                return System.IO.Path.GetFileNameWithoutExtension(exe);
            // Fallback: cmd has no quoted exe — truncate to 30 chars
            return cmd.Length > 30 ? cmd[..30] + "…" : cmd;
        }

        // Primary: (none) + all handlers (always same, always active)
        PrimaryHandlerCombo.Items.Clear();
        PrimaryHandlerCombo.Items.Add("(none)");
        foreach (var cmd in _handlerCommands)
            PrimaryHandlerCombo.Items.Add(DisplayName(cmd));

        var currentPri = NxmHandler.IsRegisteredToSelf()
            ? selfCmd : (NxmHandler.ReadCurrentCommand() ?? "");
        var priIdx = _handlerCommands.IndexOf(currentPri);
        PrimaryHandlerCombo.SelectedIndex = priIdx >= 0 ? priIdx + 1 : 0;

        // Secondary: (none) + all handlers
        SecondaryHandlerCombo.Items.Clear();
        SecondaryHandlerCombo.Items.Add("(none)");
        foreach (var cmd in _handlerCommands)
            SecondaryHandlerCombo.Items.Add(DisplayName(cmd));

        if (string.IsNullOrEmpty(_settings.NxmPreviousHandler))
            SecondaryHandlerCombo.SelectedIndex = 0;
        else
        {
            var secIdx = _handlerCommands.IndexOf(_settings.NxmPreviousHandler);
            SecondaryHandlerCombo.SelectedIndex = secIdx >= 0 ? secIdx + 1 : 0;
        }

        ApplyHandlerButton.IsEnabled = false;
    }

    private void RefreshNxmStatus()
    {
        // Sync enabled flag with actual registry state
        // (handles cases where another app took over the handler)
        var actuallyEnabled = NxmHandler.IsRegisteredToSelf();
        if (_settings.NxmHandlerEnabled != actuallyEnabled)
        {
            _settings.NxmHandlerEnabled = actuallyEnabled;
            SettingsStore.Save(_settings);
        }

        var enabled = actuallyEnabled;
        LoadHandlerDropdowns();

        PrimaryHandlerCombo.IsEnabled   = true;
        SecondaryHandlerCombo.IsEnabled = enabled;

        _suppressNxmEvent = true;
        NxmEnabledCheckBox.IsChecked = enabled;
        _suppressNxmEvent = false;

        ApplyHandlerButton.IsEnabled = false;
    }
}
