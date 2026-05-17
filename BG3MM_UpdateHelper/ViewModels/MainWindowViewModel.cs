using BG3MM_UpdateHelper.ViewModels;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using BG3MM_UpdateHelper.Models;
using BG3MM_UpdateHelper.Services;
using BG3MM_UpdateHelper.Views;
using Microsoft.Win32;

namespace BG3MM_UpdateHelper.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly Window _ownerWindow;
    private AppSettings _settings;
    private int _installedModsCount;
    private DateTime? _lastCheck = null;
    private bool _isScanning;

    // Progress state
    private string _statusText        = "";
    private int    _progressValue     = 0;
    private int    _progressMax       = 100;
    private bool   _progressIndeterminate = false;

    private List<InstalledMod> _installedMods = new();
    private readonly NexusIdDatabase _nexusIdDb   = new();
    private readonly ModFileIdStore  _fileIdStore = new();

    public MainWindowViewModel(Window ownerWindow)
    {
        _ownerWindow = ownerWindow;
        _settings    = SettingsStore.Load();
        _lastCheck   = _settings.LastCheck;

        ChangeBG3MMFolderCommand = new RelayCommand(ChangeBG3MMFolder);
        CheckUpdatesCommand      = new RelayCommand(StartCheckUpdates, () => !_isScanning);
        LaunchBG3MMCommand       = new RelayCommand(LaunchBG3MM, CanLaunchBG3MM);
        OpenSettingsCommand      = new RelayCommand(OpenSettings, () => !_isScanning);
        OpenHelpCommand          = new RelayCommand(OpenHelp);

        RecentActivities = new ObservableCollection<string>();
        AddActivity("Application started");

        _fileIdStore.Load();
        _ = RefreshModsAsync();
    }

    // ── Display properties ────────────────────────────────────────────────

    public string BG3MMFolderPath =>
        string.IsNullOrEmpty(_settings.BG3MMFolderPath) ? "(not set)" : _settings.BG3MMFolderPath;

    public string ModsFolderDisplay
    {
        get
        {
            var path = !string.IsNullOrEmpty(_settings.ModsFolderPath)
                ? _settings.ModsFolderPath
                : PathDiscovery.GetDefaultModsFolder();

            return PathDiscovery.ModsFolderExists(path)
                ? path + " (auto-detected)"
                : path + " (folder not found -- please run BG3 at least once)";
        }
    }

    public string InstalledModsCountDisplay =>
        _installedModsCount + " mod(s)";

    public string LastCheckDisplay =>
        _lastCheck.HasValue ? _lastCheck.Value.ToString("yyyy-MM-dd HH:mm") : "—";

    // ── Progress properties ───────────────────────────────────────────────

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            SetField(ref _isScanning, value);
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public int ProgressValue
    {
        get => _progressValue;
        private set => SetField(ref _progressValue, value);
    }

    public int ProgressMax
    {
        get => _progressMax;
        private set => SetField(ref _progressMax, value);
    }

    public bool ProgressIsIndeterminate
    {
        get => _progressIndeterminate;
        private set => SetField(ref _progressIndeterminate, value);
    }

    public ObservableCollection<string> RecentActivities { get; }

    // ── Commands ──────────────────────────────────────────────────────────

    public RelayCommand ChangeBG3MMFolderCommand { get; }
    public RelayCommand CheckUpdatesCommand      { get; }
    public RelayCommand LaunchBG3MMCommand       { get; }
    public RelayCommand OpenSettingsCommand      { get; }
    public RelayCommand OpenHelpCommand          { get; }

    // ── Command implementations ───────────────────────────────────────────

    private void ChangeBG3MMFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the folder containing " + Constants.BG3MM_EXE_NAME,
            InitialDirectory = string.IsNullOrEmpty(_settings.BG3MMFolderPath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : _settings.BG3MMFolderPath
        };

        if (dialog.ShowDialog() != true) return;

        if (!PathDiscovery.IsValidBG3MMFolder(dialog.FolderName))
        {
            MessageBox.Show(
                Constants.BG3MM_EXE_NAME + " was not found in the selected folder.\n\n" +
                "Please select the folder that contains BG3ModManager.exe.",
                "Invalid folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.BG3MMFolderPath = dialog.FolderName;
        SettingsStore.Save(_settings);
        OnPropertyChanged(nameof(BG3MMFolderPath));
        LibraryLoader.Initialize(dialog.FolderName);
        AddActivity("BG3MM folder updated: " + dialog.FolderName);
        Logger.Info("BG3MM folder changed to: " + dialog.FolderName);
        _ = RefreshModsAsync();
    }

    private void StartCheckUpdates() => _ = CheckUpdatesAsync();

    private async Task CheckUpdatesAsync()
    {
        if (_isScanning) return;

        // Always rescan — picks up newly installed versions
        await RefreshModsAsync();
        if (_installedMods.Count == 0)
        {
            MessageBox.Show(
                "No installed mods found. Check your BG3MM folder setting.",
                "Nothing to check", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Initialize NexusIdDatabase (sync from GitHub, 12h cache)
        StatusText = "Syncing Nexus mod database...";
        await _nexusIdDb.InitAsync();

        var hasNexus = !string.IsNullOrWhiteSpace(_settings.NexusAPIKey);
        var hasModio = !string.IsNullOrWhiteSpace(_settings.ModioAPIKey);

        if (!hasNexus && !hasModio)
        {
            MessageBox.Show(
                "No API keys configured.\n\n" +
                "Please add a Nexus Mods or mod.io API key in Settings.",
                "API keys required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsScanning              = true;
        ProgressIsIndeterminate = true;
        ProgressValue           = 0;
        StatusText              = "Checking for updates...";
        AddActivity("Checking for updates...");

        try
        {
            // Refresh Nexus Premium status once per day
            if (hasNexus &&
                DateTime.UtcNow - _settings.LastPremiumCheck > TimeSpan.FromHours(24))
            {
                StatusText = "Validating Nexus API key...";
                var nexus    = new NexusApi(_settings.NexusAPIKey);
                var userInfo = await nexus.ValidateUserAsync();
                if (userInfo != null)
                {
                    _settings.NexusIsPremium   = userInfo.IsPremium;
                    _settings.LastPremiumCheck = DateTime.UtcNow;
                    SettingsStore.Save(_settings);
                }
            }

            StatusText = "Checking for updates...";

            var nexusApi = hasNexus ? new NexusApi(_settings.NexusAPIKey) : null;
            var modioApi = hasModio ? new ModioApi(_settings.ModioAPIKey) : null;

            var progress = new Progress<int>(p =>
            {
                ProgressValue = p;
                StatusText    = "Checking for updates... (" + p + " / " + _installedMods.Count + ")";
            });

            var updates = await UpdateChecker.CheckAsync(
                _installedMods,
                nexusApi,
                modioApi,
                _nexusIdDb,
                _fileIdStore,
                _settings.NexusIsPremium,
                progress);

            _lastCheck = DateTime.Now;
            _settings.LastCheck = _lastCheck;
            SettingsStore.Save(_settings);
            OnPropertyChanged(nameof(LastCheckDisplay));

            if (updates.Count == 0)
            {
                AddActivity("All mods are up to date");
                MessageBox.Show("All mods are up to date!", "No updates",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            AddActivity(updates.Count + " update(s) available");
            Logger.Info(updates.Count + " updates found");

            // Phase 6: open update notification window
            var breakdown = new List<string>();
            var modioCount = updates.Count(u => u.AvailableSources.Contains(UpdateSource.MODIO));
            var nexusCount = updates.Count(u => u.AvailableSources.Contains(UpdateSource.NEXUSMODS));
            if (nexusCount > 0) breakdown.Add("Nexus: " + nexusCount + " mod(s)");
            if (modioCount > 0) breakdown.Add("mod.io: " + modioCount + " mod(s)");
            if (breakdown.Count > 0)
                AddActivity(string.Join(", ", breakdown));

            var folder   = !string.IsNullOrEmpty(_settings.ModsFolderPath)
                ? _settings.ModsFolderPath
                : PathDiscovery.GetDefaultModsFolder();

            // reloadFunc: rescan mods then re-run update check
            Func<Task<List<ModUpdateEntry>>> reloadFunc = async () =>
            {
                var rFolder = !string.IsNullOrEmpty(_settings.ModsFolderPath)
                    ? _settings.ModsFolderPath
                    : PathDiscovery.GetDefaultModsFolder();
                _installedMods = await ModScanner.ScanAsync(rFolder);

                var rNexusApi = hasNexus ? new NexusApi(_settings.NexusAPIKey) : null;
                var rModioApi = hasModio ? new ModioApi(_settings.ModioAPIKey) : null;
                return await UpdateChecker.CheckAsync(
                    _installedMods,
                    rNexusApi,
                    rModioApi,
                    _nexusIdDb,
                    _fileIdStore,
                    _settings.NexusIsPremium);
            };

            var notificationVm = new UpdateNotificationViewModel(
                updates,
                _settings.NexusIsPremium,
                folder,
                _settings.BackupBeforeUpdate,
                hasModio ? new ModioApi(_settings.ModioAPIKey) : null,
                hasNexus ? new NexusApi(_settings.NexusAPIKey) : null,
                _fileIdStore,
                reloadFunc);

            notificationVm.NexusMappingAdded += (uuid, modId, fileId) =>
            {
                AddActivity("Nexus linked: mod_id=" + modId);
                // TODO Phase 8: save NexusIdDatabase + Vercel contribution
            };

            var window = new Views.UpdateNotificationWindow(notificationVm)
            {
                Owner = _ownerWindow
            };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            AddActivity("Update check failed: " + ex.Message);
            Logger.Error("Update check failed: " + ex);
            MessageBox.Show("Update check failed:\n" + ex.Message,
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsScanning              = false;
            ProgressIsIndeterminate = false;
            ProgressValue           = 0;
            StatusText              = "";
        }
    }

    private void LaunchBG3MM()
    {
        try
        {
            var exePath = Path.Combine(_settings.BG3MMFolderPath, Constants.BG3MM_EXE_NAME);
            Process.Start(new ProcessStartInfo
            {
                FileName         = exePath,
                WorkingDirectory = _settings.BG3MMFolderPath,
                UseShellExecute  = true
            });
            AddActivity("BG3MM launched");
            Logger.Info("Launched BG3MM: " + exePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to launch BG3MM:\n" + ex.Message,
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Logger.Error("Failed to launch BG3MM: " + ex.Message);
        }
    }

    private bool CanLaunchBG3MM() =>
        PathDiscovery.IsValidBG3MMFolder(_settings.BG3MMFolderPath);

    private void OpenSettings()
    {
        var dialog = new SettingsWindow(_settings) { Owner = _ownerWindow };

        if (dialog.ShowDialog() != true) return;

        _settings = SettingsStore.Load();

        if (!LibraryLoader.IsInitialized && !string.IsNullOrEmpty(_settings.BG3MMFolderPath))
            LibraryLoader.Initialize(_settings.BG3MMFolderPath);

        OnPropertyChanged(nameof(BG3MMFolderPath));
        OnPropertyChanged(nameof(ModsFolderDisplay));
        AddActivity("Settings updated");
        _ = RefreshModsAsync();
    }

    private void OpenHelp()
    {
        MessageBox.Show(
            "BG3MM_UpdateHelper\n\n" +
            "A companion tool for BG3ModManager that checks for mod updates " +
            "on Nexus Mods and mod.io, and downloads them automatically.\n\n" +
            "How to use:\n" +
            "1. Configure your BG3MM folder and API keys in Settings\n" +
            "2. Click [Check for Updates]\n" +
            "3. Select mods to update and click Download\n" +
            "4. Launch BG3MM to load the updated mods\n\n" +
            "Notes:\n" +
            "- mod.io: auto-download available for all users\n" +
            "- Nexus Premium: auto-download supported\n" +
            "- Nexus Free: mod page opens for manual download",
            "Help", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── Mod scanning ──────────────────────────────────────────────────────

    private async Task RefreshModsAsync()
    {
        if (_isScanning) return;

        var folder = !string.IsNullOrEmpty(_settings.ModsFolderPath)
            ? _settings.ModsFolderPath
            : PathDiscovery.GetDefaultModsFolder();

        if (!PathDiscovery.ModsFolderExists(folder))
        {
            _installedModsCount = 0;
            OnPropertyChanged(nameof(InstalledModsCountDisplay));
            return;
        }

        if (!LibraryLoader.IsInitialized)
        {
            _installedModsCount = PathDiscovery.CountPakFiles(folder);
            OnPropertyChanged(nameof(InstalledModsCountDisplay));
            AddActivity("Found " + _installedModsCount +
                        " mod(s) (set BG3MM folder to enable full scan)");
            return;
        }

        IsScanning              = true;
        ProgressIsIndeterminate = false;
        ProgressMax             = PathDiscovery.CountPakFiles(folder);
        ProgressValue           = 0;
        StatusText              = "Scanning mods...";
        AddActivity("Scanning mods...");

        try
        {
            var progress = new Progress<int>(p =>
            {
                ProgressValue = p;
                StatusText    = "Scanning mods... (" + p + " / " + ProgressMax + ")";
            });

            _installedMods      = await ModScanner.ScanAsync(folder, progress);
            _installedModsCount = _installedMods.Count;

            // Remove fileId records for mods no longer installed
            _fileIdStore.PruneOrphans(
                _installedMods.Select(m => m.UUID).ToHashSet(StringComparer.OrdinalIgnoreCase));

            var modioCount = _installedMods.Count(m => m.PublishHandle != 0);
            AddActivity("Scan complete -- " + _installedModsCount +
                        " mod(s), " + modioCount + " on mod.io");

            OnPropertyChanged(nameof(InstalledModsCountDisplay));
        }
        catch (Exception ex)
        {
            AddActivity("Scan failed: " + ex.Message);
            Logger.Error("Scan failed: " + ex);
        }
        finally
        {
            IsScanning = false;
            StatusText = "";
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void AddActivity(string message)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            RecentActivities.Insert(0, "[" + stamp + "] " + message);
            while (RecentActivities.Count > 50)
                RecentActivities.RemoveAt(RecentActivities.Count - 1);
        });
    }
}
