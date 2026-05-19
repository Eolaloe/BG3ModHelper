using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using BG3MM_UpdateHelper.Models;
using BG3MM_UpdateHelper.Services;
using BG3MM_UpdateHelper.Views;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;

namespace BG3MM_UpdateHelper.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly Window _ownerWindow;
    private AppSettings _settings;
    private int _installedModsCount;
    private DateTime? _lastCheck = null;
    private bool _isScanning;
    private Views.UpdateNotificationWindow? _updateWindow;

    // Progress state
    private string _statusText        = "";
    private int    _progressValue     = 0;
    private int    _progressMax       = 100;
    private bool   _progressIndeterminate = false;

    private List<InstalledMod> _installedMods = [];
    private readonly NexusIdDatabase      _nexusIdDb    = new();
    private readonly ModFileIdStore       _fileIdStore  = new();
    private readonly DownloadHistoryStore _historyStore = new();
    private          FolderWatcherService? _folderWatcher;

    public MainWindowViewModel(Window ownerWindow)
    {
        _ownerWindow = ownerWindow;
        _settings    = SettingsStore.Load();
        _lastCheck   = _settings.LastCheck;

        ChangeBG3MMFolderCommand  = new RelayCommand(ChangeBG3MMFolder);
        OpenBG3MMFolderCommand    = new RelayCommand(OpenBG3MMFolder,  () => Directory.Exists(_settings.BG3MMFolderPath));
        OpenModsFolderCommand     = new RelayCommand(OpenModsFolder,   () => Directory.Exists(ModsFolderActualPath));
        CheckUpdatesCommand      = new RelayCommand(StartCheckUpdates, () => !_isScanning);
        LaunchBG3MMCommand       = new RelayCommand(LaunchBG3MM, CanLaunchBG3MM);
        OpenSettingsCommand      = new RelayCommand(OpenSettings, () => !_isScanning);
        OpenHelpCommand          = new RelayCommand(OpenHelp);
        OpenHistoryCommand       = new RelayCommand(OpenHistory);
        EnterCompactCommand      = new RelayCommand(EnterCompact);
        ExitCompactCommand       = new RelayCommand(ExitCompact);
        ExitAppCommand           = new RelayCommand(() => System.Windows.Application.Current.Shutdown());

        RecentActivities = [];
        AddActivity("Application started");

        _fileIdStore.Load();
        _historyStore.Load();
        InitFolderWatcher();
        _ = RefreshModsAsync();

        // Register nxm download handler — must be after _historyStore.Load()
        NxmDownloadQueue.Instance.SetHandler(HandleNxmItemAsync);
        NxmDownloadQueue.Instance.OnQueued += (url, size) =>
        {
            var label = size > 1
                ? $"Queued: mod={url.NexusModId} file={url.NexusFileId} (+{size - 1} already waiting)"
                : $"Queued: mod={url.NexusModId} file={url.NexusFileId}";
            AddActivity(label);
        };

        // Check nxm handler status on startup (after UI is ready)
        Application.Current.Dispatcher.InvokeAsync(CheckNxmHandler,
            System.Windows.Threading.DispatcherPriority.Background);
    }

    // === Display properties ===

    public string BG3MMFolderPath =>
        string.IsNullOrEmpty(_settings.BG3MMFolderPath) ? "(not set)" : _settings.BG3MMFolderPath;

    public string ModsFolderActualPath =>
        !string.IsNullOrEmpty(_settings.ModsFolderPath)
            ? _settings.ModsFolderPath
            : PathDiscovery.GetDefaultModsFolder();

    public string ModsFolderDisplay
    {
        get
        {
            var path = ModsFolderActualPath;
            return PathDiscovery.ModsFolderExists(path)
                ? path + " (auto-detected)"
                : path + " (folder not found -- please run BG3 at least once)";
        }
    }

    public string InstalledModsCountDisplay =>
        _installedModsCount + " mod(s)";

    public string LastCheckDisplay =>
        _lastCheck.HasValue ? _lastCheck.Value.ToString("yyyy-MM-dd HH:mm") : "—";

    // === Progress properties ===

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            SetField(ref _isScanning, value);
            CheckUpdatesCommand.RaiseCanExecuteChanged();
            OpenSettingsCommand.RaiseCanExecuteChanged();
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

    // === Commands ===

    public RelayCommand ChangeBG3MMFolderCommand { get; }
    public RelayCommand OpenBG3MMFolderCommand   { get; }
    public RelayCommand OpenModsFolderCommand    { get; }
    public RelayCommand CheckUpdatesCommand      { get; }
    public RelayCommand LaunchBG3MMCommand       { get; }
    public RelayCommand OpenSettingsCommand      { get; }
    public RelayCommand OpenHelpCommand          { get; }
    public RelayCommand OpenHistoryCommand       { get; }
    public RelayCommand EnterCompactCommand      { get; }
    public RelayCommand ExitCompactCommand       { get; }
    public RelayCommand ExitAppCommand           { get; }

    public int    CompactSize    => _settings.CompactSize;
    public double CompactOpacity
    {
        get => _settings.CompactOpacity;
        set
        {
            _settings.CompactOpacity = value;
            OnPropertyChanged();
            SettingsStore.Save(_settings);
        }
    }

    // === Command implementations ===

    private void OpenBG3MMFolder()
    {
        if (Directory.Exists(_settings.BG3MMFolderPath))
            Process.Start(new ProcessStartInfo(_settings.BG3MMFolderPath) { UseShellExecute = true });
    }

    private void OpenModsFolder()
    {
        var path = ModsFolderActualPath;
        if (Directory.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

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

        if (_updateWindow?.IsVisible == true)
        {
            _updateWindow.Activate();
            return;
        }

        // Check nxm handler status before proceeding
        CheckNxmHandler();

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
            // Refresh Nexus Premium status on every check
            if (hasNexus)
            {
                StatusText = "Validating Nexus API key...";
                var nexus    = new NexusApi(_settings.NexusAPIKey);
                var userInfo = await nexus.ValidateUserAsync();
                if (userInfo != null)
                {
                    _settings.NexusIsPremium = userInfo.IsPremium;
                    SettingsStore.Save(_settings);
                }
            }

            StatusText              = "Checking for updates...";
            ProgressIsIndeterminate = false;
            ProgressMax             = _installedMods.Count;

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
            async Task<List<ModUpdateEntry>> reloadFunc()
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
            }

            var notificationVm = new UpdateNotificationViewModel(
                updates,
                _settings.NexusIsPremium,
                folder,
                _settings.BackupBeforeUpdate,
                hasModio ? new ModioApi(_settings.ModioAPIKey) : null,
                hasNexus ? new NexusApi(_settings.NexusAPIKey) : null,
                _fileIdStore,
                _historyStore,
                reloadFunc);

            notificationVm.NexusMappingAdded += (uuid, modId, fileId) =>
            {
                AddActivity("Nexus linked: mod_id=" + modId);
                // TODO Phase 8: save NexusIdDatabase + Vercel contribution
            };

            var window = new Views.UpdateNotificationWindow(notificationVm, _settings)
            {
                Owner = _ownerWindow
            };
            _updateWindow = window;
            window.Closed += (_, _) => _updateWindow = null;
            window.Show();
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
        CheckNxmHandler();
        var dialog = new SettingsWindow(_settings, this) { Owner = _ownerWindow };

        if (dialog.ShowDialog() != true) return;

        _settings = SettingsStore.Load();

        if (!LibraryLoader.IsInitialized && !string.IsNullOrEmpty(_settings.BG3MMFolderPath))
            LibraryLoader.Initialize(_settings.BG3MMFolderPath);

        OnPropertyChanged(nameof(BG3MMFolderPath));
        OnPropertyChanged(nameof(ModsFolderDisplay));
        OnPropertyChanged(nameof(CompactOpacity));
        AddActivity("Settings updated");
        InitFolderWatcher();
        _ = RefreshModsAsync();
    }

    private void OpenHelp()
    {
        MessageBox.Show(
            "BG3MM_UpdateHelper\n\n" +
            "A companion tool for BG3ModManager that automates mod updates " +
            "from Nexus Mods and mod.io.\n\n" +
            "--- Update Check ---\n" +
            "1. Set BG3MM folder and API keys in Settings\n" +
            "2. Click [Check for Updates]\n" +
            "3. Select mods and click Download\n" +
            "4. Launch BG3MM to load the updated mods\n\n" +
            "Download support:\n" +
            "- mod.io / Nexus Premium: auto-download\n" +
            "- Nexus Free: mod page opens for manual download\n\n" +
            "--- Local Install ---\n" +
            "Drag & drop .zip / .7z / .rar / .pak files anywhere in the\n" +
            "window. Works in Compact Mode too.\n\n" +
            "--- Folder Watch ---\n" +
            "Auto-detect new archives in your download folder.\n" +
            "Enable and configure in Settings.\n\n" +
            "--- Compact Mode ---\n" +
            "Click [Compact] for a small floating window with quick\n" +
            "actions. Right-click the window for more options.\n\n" +
            "--- Other Features ---\n" +
            "- Backup: Create .pak.bak before overwriting (Settings)\n" +
            "- Recycle Bin: Move source files after install (Settings)\n" +
            "- History: View download/install log via [History]",
            "Help", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenHistory()
    {
        var vm     = new ViewModels.DownloadHistoryViewModel(_historyStore);
        var window = new Views.DownloadHistoryWindow(vm) { Owner = _ownerWindow };
        window.Show();
    }

    // === Compact Mode ===

    private Views.CompactWindow? _compactWindow;

    private void EnterCompact()
    {
        _compactWindow = new Views.CompactWindow(this)
        {
            Left = _settings.CompactX,
            Top  = _settings.CompactY,
        };
        _compactWindow.Show();
        _ownerWindow.Hide();

        _settings.LastModeIsCompact = true;
        SettingsStore.Save(_settings);
    }

    private void ExitCompact()
    {
        _compactWindow?.Close();
        _compactWindow = null;
        _ownerWindow.Show();
        _ownerWindow.Activate();

        _settings.LastModeIsCompact = false;
        SettingsStore.Save(_settings);
    }

    public void NotifyCompactOpacityChanged() => OnPropertyChanged(nameof(CompactOpacity));

    public void SaveCompactPosition(double x, double y)
    {
        _settings.CompactX = x;
        _settings.CompactY = y;
        SettingsStore.Save(_settings);
    }

    // === Folder Watcher ===

    /// <summary>Called from drag-and-drop — reuses FolderWatcherService.</summary>
    public ArchiveSourceInfo? AnalyzeDroppedArchive(string archivePath)
    {
        if (_folderWatcher != null)
            return _folderWatcher.AnalyzeArchive(archivePath);

        using var temp = new FolderWatcherService(_nexusIdDb, _ => Task.CompletedTask);
        return temp.AnalyzeArchive(archivePath);
    }

    /// <summary>Analyzes a .pak file directly for drag-and-drop install.</summary>
    public ArchiveSourceInfo? AnalyzePakFile(string pakPath)
    {
        if (_folderWatcher != null)
            return _folderWatcher.AnalyzePakFile(pakPath);

        using var temp = new FolderWatcherService(_nexusIdDb, _ => Task.CompletedTask);
        return temp.AnalyzePakFile(pakPath);
    }

    /// <summary>Enqueues an archive for install — used by drag-and-drop (bulk enqueue).</summary>
    public void AddToInstallQueue(ArchiveSourceInfo info)
    {
        _queueTotal++;
        _detectQueue.Enqueue(info);
        var pendingNames = _detectQueue.Skip(1).Select(x => x.ModName).ToList();
        _activeConfirmVm?.NotifyTotalChanged(_queueTotal, pendingNames);
    }

    /// <summary>Starts processing the install queue if not already running.</summary>
    public async Task ProcessInstallQueue()
    {
        if (_processingQueue) return;

        // Bring main window to front
        if (_ownerWindow.WindowState == System.Windows.WindowState.Minimized)
            _ownerWindow.WindowState = System.Windows.WindowState.Normal;
        _ownerWindow.Topmost = true;
        _ownerWindow.Activate();
        _ownerWindow.Topmost = false;

        _processingQueue = true;
        int currentIndex = 0;

        while (_detectQueue.Count > 0)
        {
            currentIndex++;
            var next     = _detectQueue.Peek();
            var remaining = _detectQueue.Skip(1).Select(x => x.ModName).ToList();

            var vm = new ViewModels.InstallConfirmViewModel(next, currentIndex, _queueTotal, remaining);
            _activeConfirmVm = vm;

            var window = new Views.InstallConfirmWindow(vm) { Owner = _ownerWindow };

            vm.InstallAllRequested += async () =>
            {
                window.Close();
                // Remove `next` from queue first — otherwise the while loop below
                // would dequeue and reinstall it as the first remaining item.
                _detectQueue.TryDequeue(out _);
                await InstallLocalArchiveAsync(next, vm.FinalSource);
                while (_detectQueue.Count > 0)
                {
                    var r = _detectQueue.Dequeue();
                    await InstallLocalArchiveAsync(r, r.Source == "Both" ? r.ZipHintSource ?? r.Source : r.Source);
                }
            };
            vm.IgnoreAllRequested += () =>
            {
                _detectQueue.Clear();
                window.Close();
            };

            window.ShowDialog();
            _detectQueue.TryDequeue(out _);

            if (window.Confirmed && !vm.InstallAllFired)
                await InstallLocalArchiveAsync(next, window.FinalSource);
        }

        _activeConfirmVm = null;
        _processingQueue = false;
        _queueTotal      = 0;
    }

    private void InitFolderWatcher()
    {
        _folderWatcher?.Dispose();

        if (!_settings.FolderWatchEnabled) return;

        var folder = !string.IsNullOrEmpty(_settings.WatchedDownloadFolder)
            ? _settings.WatchedDownloadFolder
            : FolderWatcherService.GetDefaultDownloadsFolder();

        _folderWatcher = new FolderWatcherService(_nexusIdDb, OnArchiveDetected);
        _folderWatcher.Start(folder);
        Logger.Info($"FolderWatcher started: {folder}");
    }

    // === Install confirmation queue ===

    private readonly Queue<ArchiveSourceInfo>  _detectQueue  = new();
    private          InstallConfirmViewModel?  _activeConfirmVm;
    private          bool                      _processingQueue;
    private          int                       _queueTotal;

    private async Task OnArchiveDetected(ArchiveSourceInfo info)
    {
        await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            AddToInstallQueue(info);
            await ProcessInstallQueue();
        });
    }

    public async Task InstallLocalArchiveAsync(ArchiveSourceInfo info, string finalSource)
    {
        var modsFolder = !string.IsNullOrEmpty(_settings.ModsFolderPath)
            ? _settings.ModsFolderPath
            : PathDiscovery.GetDefaultModsFolder();

        // Find current version from installed mods
        var existing = _installedMods.FirstOrDefault(m =>
            string.Equals(Path.GetFileName(m.PakFilePath), info.PakFileName,
                          StringComparison.OrdinalIgnoreCase));
        var fromVersion = existing?.MetaVersion ?? "Not installed";

        // Resolve platform mod name and page URL
        // Nexus/Both-as-Nexus: name + URL already in info from local DB
        // ModIO (user explicitly chose mod.io): one API call to get name + profile URL
        var platformModName = info.PlatformModName;
        var pageUrl         = info.NexusPageUrl;
        if (finalSource == "ModIO" &&
            info.PublishHandle != 0 &&
            !string.IsNullOrEmpty(_settings.ModioAPIKey))
        {
            try
            {
                var modioApi  = new ModioApi(_settings.ModioAPIKey);
                var modioData = await modioApi.GetModInfoAsync(info.PublishHandle);
                if (modioData != null)
                {
                    platformModName = modioData.ModioModName;
                    pageUrl         = modioData.ModioProfileUrl;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"mod.io lookup failed for handle {info.PublishHandle}: {ex.Message}");
            }
        }

        IsScanning  = true;
        StatusText  = $"Installing {info.ModName}...";
        try
        {
            var progress = new Progress<DownloadProgress>(p => StatusText = p.Text);

            // pak direct copy vs archive extraction
            if (Path.GetExtension(info.ArchivePath).Equals(".pak", StringComparison.OrdinalIgnoreCase))
            {
                var destPath = Path.Combine(modsFolder, info.PakFileName);
                if (_settings.BackupBeforeUpdate && File.Exists(destPath))
                    File.Copy(destPath, destPath + ".bak", overwrite: true);
                await Task.Run(() => File.Copy(info.ArchivePath, destPath, overwrite: true));
                Logger.Info($"pak direct install: {info.PakFileName}");
            }
            else
            {
                await Downloader.InstallLocalArchiveAsync(
                    info.ArchivePath, modsFolder, _settings.BackupBeforeUpdate, progress);
            }

            _historyStore.Add(new Models.DownloadHistoryEntry
            {
                HistoryDownloadedAt    = DateTime.UtcNow,
                HistoryModName         = info.ModName,
                HistoryPlatformModName = platformModName,
                HistoryFromVersion     = fromVersion,
                HistoryToVersion       = !string.IsNullOrEmpty(info.ModVersion) ? info.ModVersion : "—",
                HistorySource          = finalSource,
                HistoryPageUrl         = pageUrl,
                HistorySuccess         = true,
            });

            AddActivity($"Installed: {info.ModName}");
            Logger.Info($"Local install complete: {info.ModName}");

            // Move source file to Recycle Bin after successful install (if enabled)
            if (_settings.DeleteSourceAfterInstall && File.Exists(info.ArchivePath))
            {
                try
                {
                    FileSystem.DeleteFile(info.ArchivePath,
                        UIOption.OnlyErrorDialogs,
                        RecycleOption.SendToRecycleBin);
                    Logger.Info($"Source moved to Recycle Bin: {Path.GetFileName(info.ArchivePath)}");
                    AddActivity($"Source moved to Recycle Bin: {Path.GetFileName(info.ArchivePath)}");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Failed to move source to Recycle Bin {info.ArchivePath}: {ex.Message}");
                }
            }

            _ = RefreshModsAsync();
        }
        catch (Exception ex)
        {
            _historyStore.Add(new Models.DownloadHistoryEntry
            {
                HistoryDownloadedAt    = DateTime.UtcNow,
                HistoryModName         = info.ModName,
                HistoryPlatformModName = platformModName,
                HistoryFromVersion     = fromVersion,
                HistoryToVersion       = "—",
                HistorySource          = finalSource,
                HistoryPageUrl         = pageUrl,
                HistorySuccess         = false,
            });
            MessageBox.Show($"Installation failed:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Logger.Error($"Local install failed: {info.ModName} — {ex.Message}");
        }
        finally
        {
            IsScanning = false;
            StatusText = "";
            ProgressValue = 0;
        }
    }

    /// <summary>Restarts folder watcher after settings are saved.</summary>
    public void RestartFolderWatcher() => InitFolderWatcher();

    // === Mod scanning ===

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
                _installedMods.Select(m => m.MetaUuid).ToHashSet(StringComparer.OrdinalIgnoreCase));

            var modioCount = _installedMods.Count(m => m.ModioPublishHandle != 0);
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

    // === nxm handler check ===

    /// <summary>
    /// Checks if the nxm:// handler has been taken over by another manager.
    /// Called on startup, Settings open, and Check for Updates.
    /// </summary>
    public void CheckNxmHandler()
    {
        var settings = _settings;
        if (!settings.NxmHandlerEnabled) return;
        if (NxmHandler.IsRegisteredToSelf()) return;

        // Handler was taken over — show dialog
        var current    = NxmHandler.ReadCurrentCommand() ?? "";
        var exe        = NxmHandler.ExtractExePath(current);
        var name       = string.IsNullOrEmpty(exe)
            ? "another application"
            : System.IO.Path.GetFileNameWithoutExtension(exe);

        Logger.Warn($"NxmHandler: taken over by {name}");

        var result = MessageBox.Show(
            $"{name} has taken over the nxm:// handler.\n\n" +
            "BG3 'Download with Manager' links will no longer reach this app.\n\n" +
            "Re-register this app as the primary handler?",
            "nxm:// Handler Conflict",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            // Add the new intruder to KnownHandlers before overwriting
            if (!string.IsNullOrEmpty(current) &&
                !settings.NxmKnownHandlers.Contains(current))
                settings.NxmKnownHandlers.Add(current);

            settings.NxmPreviousHandler = current;
            NxmHandler.EnableHandler(settings);
            AddActivity($"nxm handler re-registered (was: {name})");
        }
        else
        {
            AddActivity($"nxm handler taken by {name} — re-register in Settings");
        }
    }

    // === Helpers ===

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

    // === nxm download handler ===

    private async Task HandleNxmItemAsync(NxmQueueItem item)
    {
        // Bring main window to front (may be hidden or behind)
        Application.Current.Dispatcher.Invoke(() =>
        {
            _ownerWindow.Show();
            _ownerWindow.Activate();
        });

        AddActivity($"Downloading from Nexus: mod={item.Url.NexusModId} file={item.Url.NexusFileId}");

        var progress = new Progress<DownloadProgress>(p =>
        {
            var waiting = NxmDownloadQueue.Instance.PendingCount;
            var prefix  = waiting > 0 ? $"[+{waiting} queued] " : "";
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                StatusText    = prefix + p.Text;
                ProgressValue = p.Percent;
            });
            NxmDownloadQueue.Instance.ReportProgress(item, p);
        });

        var result      = await NxmInstaller.HandleAsync(item, progress);
        var errorReason = result is null ? NxmInstaller.ConsumeLastError() : null;

        // NxmInstaller가 별도 인스턴스로 fileIdStore를 저장했으므로 디스크에서 다시 로드
        if (result is not null) _fileIdStore.Load();

        NxmDownloadQueue.Instance.NotifyCompleted(item, result is not null, errorReason);

        if (result is not null)
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                StatusText    = "";
                ProgressValue = 0;
            });

            AddActivity($"Installed from Nexus: {result.UpdateModName}");

            var pageUrl        = $"https://www.nexusmods.com/baldursgate3/mods/{result.NexusModId}";
            var nxmFromVersion =
                _historyStore.GetAll()
                    .Where(h => h.HistorySuccess && h.HistoryPageUrl == pageUrl)
                    .OrderByDescending(h => h.HistoryDownloadedAt)
                    .FirstOrDefault()?.HistoryToVersion
                ?? _installedMods
                    .FirstOrDefault(m => m.NexusModId.HasValue && m.NexusModId.Value == item.Url.NexusModId)
                    ?.MetaVersion
                ?? "";
            var nxmPlatformName = _nexusIdDb
                .LookupSingle(Path.GetFileNameWithoutExtension(result.PakFileName))
                ?.NexusModName ?? "";

            _historyStore.Add(new Models.DownloadHistoryEntry
            {
                HistoryDownloadedAt    = DateTime.UtcNow,
                HistoryModName         = result.UpdateModName,
                HistoryPlatformModName = nxmPlatformName,
                HistoryFromVersion     = string.IsNullOrEmpty(nxmFromVersion) ? "Not installed" : nxmFromVersion,
                HistoryToVersion       = result.UpdateNewVersion,
                HistorySource          = "Nexus",
                HistoryPageUrl         = pageUrl,
                HistorySuccess         = true,
            });

            _ = RefreshModsAsync();
        }
        else
        {
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                StatusText    = "";
                ProgressValue = 0;
            });

            AddActivity($"Download failed: mod={item.Url.NexusModId}");

            var failPageUrl    = $"https://www.nexusmods.com/baldursgate3/mods/{item.Url.NexusModId}";
            var nxmFromVersionFail =
                _historyStore.GetAll()
                    .Where(h => h.HistorySuccess && h.HistoryPageUrl == failPageUrl)
                    .OrderByDescending(h => h.HistoryDownloadedAt)
                    .FirstOrDefault()?.HistoryToVersion
                ?? _installedMods
                    .FirstOrDefault(m => m.NexusModId.HasValue && m.NexusModId.Value == item.Url.NexusModId)
                    ?.MetaVersion
                ?? "";
            var failMod          = _installedMods.FirstOrDefault(m => m.NexusModId.HasValue && m.NexusModId.Value == item.Url.NexusModId);
            var failPakBase      = failMod != null ? Path.GetFileNameWithoutExtension(failMod.PakFilePath ?? "") : "";
            var failDbEntry      = !string.IsNullOrEmpty(failPakBase) ? _nexusIdDb.LookupSingle(failPakBase) : null;
            var failPlatformName = failDbEntry?.NexusModName ?? "";
            var failModName      = failMod?.MetaModuleName ?? failDbEntry?.NexusModName ?? $"Mod {item.Url.NexusModId}";

            _historyStore.Add(new Models.DownloadHistoryEntry
            {
                HistoryDownloadedAt    = DateTime.UtcNow,
                HistoryModName         = failModName,
                HistoryPlatformModName = failPlatformName,
                HistoryFromVersion     = string.IsNullOrEmpty(nxmFromVersionFail) ? "Not installed" : nxmFromVersionFail,
                HistoryToVersion       = "",
                HistorySource          = "Nexus",
                HistoryPageUrl         = failPageUrl,
                HistorySuccess         = false,
            });
        }
    }
}
