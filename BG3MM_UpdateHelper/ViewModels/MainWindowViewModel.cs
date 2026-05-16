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
    private readonly NexusIdDatabase _nexusIdDb  = new();

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

        // 항상 재스캔 — 설치/업데이트 후 최신 버전 반영
        await RefreshModsAsync();
        if (_installedMods.Count == 0)
        {
            MessageBox.Show(
                "No installed mods found. Check your BG3MM folder setting.",
                "Nothing to check", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // NexusIdDatabase 초기화 (GitHub DB 동기화, 12h 캐시)
        StatusText = "Syncing Nexus mod database...";
        await _nexusIdDb.InitAsync();

        // 24시간마다 Nexus 히스토리 수집
        if (DateTime.UtcNow - _settings.LastNexusHistorySync > TimeSpan.FromHours(24)
            && !string.IsNullOrWhiteSpace(_settings.NexusAPIKey))
        {
            StatusText = "Syncing Nexus download history...";
            Logger.Info("CheckUpdates: starting Nexus history sync");
            try
            {
                var history = await NexusWebLogin.LoginAndFetchHistoryAsync(_ownerWindow);
                if (history != null)
                {
                    var nexusForDb = new NexusApi(_settings.NexusAPIKey);
                    var histProg   = new Progress<(int done, int total)>(p =>
                        StatusText = $"Matching Nexus mods... ({p.done}/{p.total})");
                    var matched = await _nexusIdDb.SyncFromHistoryAsync(
                        history, _installedMods, nexusForDb, histProg);
                    if (matched > 0)
                        AddActivity($"Nexus: {matched} new mod(s) linked");
                    _settings.LastNexusHistorySync = DateTime.UtcNow;
                    SettingsStore.Save(_settings);
                }
            }
            catch (Exception ex) { Logger.Warn($"Nexus history sync failed: {ex.Message}"); }
        }

        // InstalledMod에 NexusModId 주입 (DB에서 조회)
        foreach (var mod in _installedMods)
            if (!mod.NexusModId.HasValue)
                mod.NexusModId = _nexusIdDb.GetNexusModId(mod.UUID);

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

            // Switch to determinate once we know how many mods to check
            ProgressIsIndeterminate = false;
            ProgressMax             = _installedMods.Count;
            ProgressValue           = 0;
            StatusText              = "Querying mod.io and Nexus Mods...";

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
                _settings.NexusIsPremium,
                _settings.CacheExpiryHours,
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

            // Phase 6: 업데이트 알림 창 열기
            var breakdown = new List<string>();
            var modioCount = updates.Count(u => u.AvailableSources.Contains(UpdateSource.MODIO));
            var nexusCount = updates.Count(u => u.AvailableSources.Contains(UpdateSource.NEXUSMODS));
            if (modioCount > 0) breakdown.Add("mod.io: " + modioCount + "개");
            if (nexusCount > 0) breakdown.Add("Nexus: " + nexusCount + "개");
            if (breakdown.Count > 0)
                AddActivity("  " + string.Join(", ", breakdown));

            var folder   = !string.IsNullOrEmpty(_settings.ModsFolderPath)
                ? _settings.ModsFolderPath
                : PathDiscovery.GetDefaultModsFolder();

            var notificationVm = new UpdateNotificationViewModel(
                updates,
                _settings.NexusIsPremium,
                folder,
                _settings.BackupBeforeUpdate,
                hasModio ? new ModioApi(_settings.ModioAPIKey) : null,
                hasNexus ? new NexusApi(_settings.NexusAPIKey) : null);

            notificationVm.NexusMappingAdded += (uuid, modId, fileId) =>
            {
                AddActivity("Nexus linked: mod_id=" + modId);
                // TODO Phase 8: NexusIdDatabase 저장 + Vercel 기여
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
            IsScanning = false;
            StatusText = "";
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

        // 초기 연동 상태 표시
        var isLinked = _settings.LastNexusHistorySync > DateTime.MinValue;
        dialog.SetNexusStatus(
            isLinked
                ? $"✓ Linked  ({_settings.LastNexusHistoryCount} mods · last sync: {_settings.LastNexusHistorySync.ToLocalTime():MM/dd HH:mm})"
                : "Not linked",
            isLinked);

        dialog.NexusLinkRequested += async (_, _) =>
        {
            dialog.SetNexusStatus("Connecting...", false);
            try
            {
                var history = await NexusWebLogin.LoginAndFetchHistoryAsync(dialog);
                if (history != null)
                {
                    dialog.SetNexusStatus($"✓ Linked  ({history.Count} mods in history)", true);
                    _settings.LastNexusHistorySync  = DateTime.UtcNow;
                    _settings.LastNexusHistoryCount = history.Count;
                    SettingsStore.Save(_settings);
                }
                else
                    dialog.SetNexusStatus("Cancelled", false);
            }
            catch (Exception ex)
            {
                dialog.SetNexusStatus("Failed", false);
                Logger.Warn($"Settings Nexus link: {ex.Message}");
            }
        };

        if (dialog.ShowDialog() != true) return;

        _settings = SettingsStore.Load();
        OnPropertyChanged(nameof(BG3MMFolderPath));
        OnPropertyChanged(nameof(ModsFolderDisplay));
        AddActivity("Settings updated");
        _ = RefreshModsAsync();
    }

    private void OpenHelp()
    {
        MessageBox.Show(
            "BG3MM_UpdateHelper\n\n" +
            "A companion tool for BG3ModManager that checks for mod updates\n" +
            "on Nexus Mods and mod.io, and downloads them automatically.\n\n" +
            "How to use:\n" +
            "1. Configure your BG3MM folder and API keys in Settings\n" +
            "2. Click [Check for Updates]\n" +
            "3. Select mods to update and click Download\n" +
            "4. Launch BG3MM to load the updated mods",
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
