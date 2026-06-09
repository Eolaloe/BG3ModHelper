using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using BG3ModHelper.Models;
using BG3ModHelper.Services;
using BG3ModHelper.Views;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;

namespace BG3ModHelper.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly Window _ownerWindow;
    private AppSettings _settings;
    private int _installedModsCount;
    private DateTime? _lastCheck = null;
    private bool _isScanning;
    private int  _scanRefCount; // reference-counted: IsScanning stays true until all callers release
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
    private readonly UserModLinkStore     _userLinks    = new();
    private          FolderWatcherService? _folderWatcher;

    // === Nexus API rate limit display ===
    private int _nexusHourlyRemaining = NexusApi.LastKnownHourlyRemaining;
    private int _nexusDailyRemaining  = NexusApi.LastKnownDailyRemaining;

    public string NexusRateLimitText =>
        _nexusHourlyRemaining < 0 ? "API —" :
        _nexusDailyRemaining == 0
            ? $"API  {_nexusHourlyRemaining:N0}/h  (daily exhausted)"
            : $"API  {_nexusHourlyRemaining:N0}h · {_nexusDailyRemaining:N0}d";

    public string NexusRateLimitColor =>
        (_nexusHourlyRemaining >= 0 && _nexusHourlyRemaining <= 10) ||
        (_nexusDailyRemaining  >= 0 && _nexusDailyRemaining  <= 100) ? "#c42b2b" :
        (_nexusHourlyRemaining >= 0 && _nexusHourlyRemaining <= 50)  ||
        (_nexusDailyRemaining  >= 0 && _nexusDailyRemaining  <= 500) ? "#e07000" :
        "#888888";

    private bool _rateLimitChecking;
    public bool RateLimitChecking
    {
        get => _rateLimitChecking;
        private set { _rateLimitChecking = value; OnPropertyChanged(); }
    }

    public RelayCommand CheckRateLimitsCommand { get; private set; } = null!;

    private void OnNexusRateLimitsUpdated(int hourly, int daily)
    {
        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            _nexusHourlyRemaining = hourly;
            _nexusDailyRemaining  = daily;
            OnPropertyChanged(nameof(NexusRateLimitText));
            OnPropertyChanged(nameof(NexusRateLimitColor));
        });
    }

    private async void ExecuteCheckRateLimits()
    {
        if (string.IsNullOrEmpty(_settings.NexusAPIKey) || RateLimitChecking) return;
        RateLimitChecking = true;
        try   { await new NexusApi(_settings.NexusAPIKey).ValidateUserAsync(); }
        catch { }
        finally { RateLimitChecking = false; }
    }

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
        OpenInstalledModsCommand = new RelayCommand(OpenInstalledMods);
        EnterCompactCommand      = new RelayCommand(EnterCompact);
        ExitCompactCommand       = new RelayCommand(ExitCompact);
        ExitAppCommand           = new RelayCommand(() => System.Windows.Application.Current.Shutdown());
        CheckRateLimitsCommand   = new RelayCommand(ExecuteCheckRateLimits,
            () => !string.IsNullOrEmpty(_settings.NexusAPIKey) && !RateLimitChecking);

        NexusApi.RateLimitsUpdated += OnNexusRateLimitsUpdated;

        RecentActivities = [];
        AddActivity("Application started");

        _fileIdStore.Load();
        _historyStore.Load();
        InitFolderWatcher();
        _ = StartupInitAsync();

        // Register nxm download handler — must be after _historyStore.Load()
        UnifiedDownloadQueue.Instance.SetNxmHandler(HandleNxmItemAsync);
        UnifiedDownloadQueue.Instance.OnNxmQueued += (url, size) =>
        {
            var label = size > 1
                ? $"Queued: mod={url.NexusModId} file={url.NexusFileId} (+{size - 1} already waiting)"
                : $"Queued: mod={url.NexusModId} file={url.NexusFileId}";
            AddActivity(label);
        };
        // All download types report through OnProgress — update the main window progress bar
        UnifiedDownloadQueue.Instance.OnProgress += p =>
        {
            var waiting = UnifiedDownloadQueue.Instance.PendingCount;
            var prefix  = waiting > 0 ? $"[+{waiting} queued] " : "";
            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                StatusText    = prefix + p.Text;
                ProgressValue = p.Percent;
            });
            // Log successful auto-update completions — fires once per mod when p.Text=="Updated"
            if (p.Percent == 100 && p.Text == "Updated")
            {
                var job = UnifiedDownloadQueue.Instance.CurrentJob;
                if (job?.Kind == Services.DownloadJobKind.AutoUpdate)
                    AddActivity($"Updated: {job.Label}");
            }
        };
        // Drive IsScanning (compact arc + main spinner) for the duration of each queue job
        UnifiedDownloadQueue.Instance.OnJobStarted += job =>
        {
            BeginScanning();
            // Invoke (synchronous/blocking) ensures ProgressMax=100 is applied on the UI
            // thread BEFORE Execute() starts — InvokeAsync would race against the first
            // progress reports if the download URL fetch completes quickly.
            Application.Current.Dispatcher.Invoke(() =>
            {
                ProgressIsIndeterminate = false;
                ProgressMax             = 100;
                ProgressValue           = 0;
            });
            if (job.Kind == Services.DownloadJobKind.AutoUpdate)
                AddActivity($"Downloading: {job.Label}");
        };
        UnifiedDownloadQueue.Instance.OnJobFinished += _job =>
        {
            EndScanning();
            _ = Application.Current.Dispatcher.InvokeAsync(() =>
            {
                StatusText    = "";
                ProgressValue = 0;
            });
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

    public string InstalledModsCountDisplay
    {
        get
        {
            return $"{_installedModsCount} mod(s)";
        }
    }

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
    public RelayCommand OpenInstalledModsCommand { get; }
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
        LibraryLoader.SetBg3mmFolder(dialog.FolderName);
        AddActivity("BG3MM folder updated: " + dialog.FolderName);
        Logger.Info("BG3MM folder changed to: " + dialog.FolderName);
        _ = RefreshModsAsync();
    }

    /// <summary>
    /// Runs the initial scan and DB init concurrently so the UI shows mod count quickly,
    /// then runs a second NexusModId resolution pass once both are guaranteed ready.
    /// This avoids the race where the pak scan finishes before the DB ETag response
    /// arrives, leaving ambiguous pak lookups unresolved.
    /// </summary>
    private async Task StartupInitAsync()
    {
        // Both run in parallel — scan gives quick mod count, DB init fetches/validates cache
        await Task.WhenAll(RefreshModsAsync(), _nexusIdDb.InitAsync());
        // Second pass: fill in NexusModIds that the scan missed because DB wasn't ready yet
        ApplyNexusIdsFromDb();

        // Fire-and-forget: fetch real rate limit counts on startup (1 call)
        if (!string.IsNullOrEmpty(_settings.NexusAPIKey))
            _ = new NexusApi(_settings.NexusAPIKey).ValidateUserAsync();
    }

    /// <summary>
    /// Lightweight post-scan pass: resolves NexusModId for any mods that still have null
    /// after the main scan.  Safe to call multiple times — skips mods already resolved.
    /// </summary>
    private void ApplyNexusIdsFromDb()
    {
        if (_installedMods.Count == 0) return;
        var dbChanged = false;

        // Pre-pass: evict any cached NexusModIds that are now in the DB's _removed list.
        foreach (var mod in _installedMods)
        {
            if (mod.NexusModId.HasValue && _nexusIdDb.IsRemovedOnNexus(mod.NexusModId.Value))
            {
                Logger.Info($"NexusIdDatabase: clearing removed mod ID {mod.NexusModId} from {mod.PakFileName}");
                mod.NexusModId = null;
                dbChanged = true;
            }
        }

        foreach (var mod in _installedMods)
        {
            if (mod.NexusModId.HasValue) continue;

            var entry = _nexusIdDb.LookupSingle(mod.PakFileName);
            if (entry != null)                 { mod.NexusModId = entry.NexusModId; dbChanged = true; continue; }

            var sharedId = _nexusIdDb.LookupUnambiguousModId(mod.PakFileName);
            if (sharedId.HasValue)             { mod.NexusModId = sharedId;         dbChanged = true; continue; }

            var authorId = _nexusIdDb.LookupByPakAndAuthor(mod.PakFileName, mod.MetaAuthor);
            if (authorId.HasValue)             { mod.NexusModId = authorId;          dbChanged = true; continue; }

            var modNameId = _nexusIdDb.LookupByPakAndModName(mod.PakFileName, mod.MetaModuleName);
            if (modNameId.HasValue)            { mod.NexusModId = modNameId;         dbChanged = true; continue; }

            var stored = _fileIdStore.GetEntry(mod.MetaUuid);
            if (stored?.NexusModId > 0)        { mod.NexusModId = stored.NexusModId; dbChanged = true; }
        }

        // Cross-pak author correlation: same logic as in RefreshModsAsync — see comment there.
        var authorKnownIds = _installedMods
            .Where(m => m.NexusModId.HasValue &&
                        !string.IsNullOrWhiteSpace(m.MetaAuthor) &&
                        m.MetaAuthor != "—")
            .GroupBy(m => m.MetaAuthor!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(m => m.NexusModId!.Value).Distinct().ToList(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var mod in _installedMods)
        {
            if (mod.NexusModId.HasValue) continue;
            if (string.IsNullOrWhiteSpace(mod.MetaAuthor) || mod.MetaAuthor == "—") continue;
            if (!authorKnownIds.TryGetValue(mod.MetaAuthor, out var knownIds)) continue;
            if (knownIds.Count != 1) continue;
            var knownId = knownIds[0];
            var candidates = _nexusIdDb.LookupByPakFileName(mod.PakFileName);
            if (candidates.Any(e => e.NexusModId == knownId))
            {
                mod.NexusModId = knownId;
                dbChanged = true;
            }
        }

        if (dbChanged) ModScanner.SaveNexusIds(_installedMods);
    }

    private void StartCheckUpdates() => _ = CheckUpdatesAsync();

    private async Task CheckUpdatesAsync()
    {
        if (_scanRefCount > 0) return;

        if (_updateWindow?.IsVisible == true)
        {
            _updateWindow.Activate();
            return;
        }

        // Check nxm handler status before proceeding
        CheckNxmHandler();

        // Initialize NexusIdDatabase first so scan-time lookup works
        StatusText = "Syncing Nexus mod database...";
        await _nexusIdDb.InitAsync();

        // Always rescan — picks up newly installed versions (DB must be ready first)
        await RefreshModsAsync();
        if (_installedMods.Count == 0)
        {
            var scannedFolder = !string.IsNullOrEmpty(_settings.ModsFolderPath)
                ? _settings.ModsFolderPath
                : PathDiscovery.GetDefaultModsFolder();
            Logger.Warn($"CheckUpdates: no mods found in — {scannedFolder}");
            MessageBox.Show(
                $"No installed mods found.\n\nScanned folder:\n{scannedFolder}\n\nIf this is wrong, go to Settings → Mods Folder Path and set the correct path.\n(Default: %LocalAppData%\\Larian Studios\\Baldur's Gate 3\\Mods)",
                "Nothing to check", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

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

        BeginScanning();
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
                progress,
                _userLinks);

            // Mark active mods based on load order
            var activeUuids = new HashSet<string>(
                ModSettingsParser.GetLoadOrder(ModSettingsParser.GetDefaultPath()),
                StringComparer.OrdinalIgnoreCase);
            foreach (var u in updates)
                u.IsActive = activeUuids.Contains(u.MetaUuid);

            _lastCheck = DateTime.Now;
            _settings.LastCheck = _lastCheck;
            SettingsStore.Save(_settings);
            OnPropertyChanged(nameof(LastCheckDisplay));

            // Persist NexusModIds populated during this check
            var _saveIdsSw = System.Diagnostics.Stopwatch.StartNew();
            ModScanner.SaveNexusIds(_installedMods);
            Logger.Debug($"[PERF] SaveNexusIds: {_saveIdsSw.ElapsedMilliseconds}ms");
            _installedModsCount = _installedMods.Count;
            OnPropertyChanged(nameof(InstalledModsCountDisplay));

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
                    _settings.NexusIsPremium,
                    userLinkStore: _userLinks);
            }

            var _sw = System.Diagnostics.Stopwatch.StartNew();

            var notificationVm = new UpdateNotificationViewModel(
                updates,
                _settings.NexusIsPremium,
                folder,
                _settings.BackupBeforeUpdate,
                hasModio ? new ModioApi(_settings.ModioAPIKey) : null,
                hasNexus ? new NexusApi(_settings.NexusAPIKey) : null,
                _fileIdStore,
                _historyStore,
                reloadFunc,
                _userLinks,
                _nexusIdDb);
            Logger.Debug($"[PERF] UpdateNotificationViewModel ctor: {_sw.ElapsedMilliseconds}ms");
            _sw.Restart();

            notificationVm.NexusMappingAdded += (uuid, modId, fileId) =>
            {
                AddActivity("Nexus linked: mod_id=" + modId);
                // TODO Phase 8: save NexusIdDatabase + Vercel contribution
            };

            var window = new Views.UpdateNotificationWindow(notificationVm, _settings)
            {
                Owner = _ownerWindow
            };
            Logger.Debug($"[PERF] UpdateNotificationWindow ctor: {_sw.ElapsedMilliseconds}ms");
            _sw.Restart();

            _updateWindow = window;
            window.Closed += (_, _) => _updateWindow = null;
            window.Show();
            Logger.Debug($"[PERF] window.Show(): {_sw.ElapsedMilliseconds}ms");

            // Fetch changelogs in background — window is already open.
            // Tooltips re-evaluate on each hover, so values will appear once they arrive.
            if (nexusApi != null)
            {
                _ = Task.WhenAll(updates
                    .Where(u => u.NexusModId.HasValue &&
                                u.AvailableSources.Contains(UpdateSource.NEXUSMODS))
                    .Select(u => nexusApi.GetChangelogAsync(u.NexusModId!.Value, u.NexusFileVersion)
                        .ContinueWith(t => { if (t.Result != null) u.Changelog = t.Result; })));
            }
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
            EndScanning();
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

        var oldBg3mmPath = _settings.BG3MMFolderPath;
        var oldModsPath  = _settings.ModsFolderPath;

        _settings = SettingsStore.Load();

        if (!string.IsNullOrEmpty(_settings.BG3MMFolderPath))
            LibraryLoader.SetBg3mmFolder(_settings.BG3MMFolderPath);

        OnPropertyChanged(nameof(BG3MMFolderPath));
        OnPropertyChanged(nameof(ModsFolderDisplay));
        OnPropertyChanged(nameof(CompactOpacity));
        AddActivity("Settings updated");
        InitFolderWatcher();

        if (oldBg3mmPath != _settings.BG3MMFolderPath || oldModsPath != _settings.ModsFolderPath)
            _ = RefreshModsAsync();
    }

    private void OpenHelp()
    {
        var window = new Views.GuideWindow { Owner = _ownerWindow };
        window.ShowDialog();
    }


    private void OpenHistory()
    {
        var vm     = new ViewModels.DownloadHistoryViewModel(_historyStore);
        var window = new Views.DownloadHistoryWindow(vm) { Owner = _ownerWindow };
        window.Show();
    }

    private async void OpenInstalledMods()
    {
        var mods = _installedMods.ToList();  // snapshot — VM ctor runs off UI thread
        var vm   = await Task.Run(() => new ViewModels.InstalledModsViewModel(mods, _fileIdStore, _nexusIdDb, _userLinks));
        var window = new Views.InstalledModsWindow(vm) { Owner = _ownerWindow };
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


    public void SaveCompactPosition(double x, double y)
    {
        _settings.CompactX = x;
        _settings.CompactY = y;
        SettingsStore.Save(_settings);
    }

    // === Folder Watcher ===

    /// <summary>Called from drag-and-drop — reuses FolderWatcherService.</summary>
    public async Task<ArchiveSourceInfo?> AnalyzeDroppedArchive(string archivePath)
    {
        if (_folderWatcher != null)
            return await _folderWatcher.AnalyzeArchive(archivePath);

        using var temp = new FolderWatcherService(_nexusIdDb, _ => Task.CompletedTask, _settings.NexusAPIKey);
        return await temp.AnalyzeArchive(archivePath);
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

    /// <summary>
    /// Routes the pending install queue through UnifiedDownloadQueue so local-archive
    /// installs are serialized with NXM and auto-update downloads.
    /// If a ProcessInstallQueue call is already running its while-loop it will pick up
    /// any newly added items; no second job is needed.
    /// </summary>
    public void EnqueueInstallBatch()
    {
        if (_processingQueue) return;

        UnifiedDownloadQueue.Instance.Enqueue(new Services.DownloadJob
        {
            Kind    = Services.DownloadJobKind.LocalArchive,
            Label   = "Local archive install",
            Execute = async () =>
            {
                var tcs = new TaskCompletionSource();
                _ = Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    try   { await ProcessInstallQueue(); }
                    finally { tcs.TrySetResult(); }
                });
                await tcs.Task;
            }
        });
    }

    private void InitFolderWatcher()
    {
        _folderWatcher?.Dispose();

        if (!_settings.FolderWatchEnabled) return;

        var folder = !string.IsNullOrEmpty(_settings.WatchedDownloadFolder)
            ? _settings.WatchedDownloadFolder
            : FolderWatcherService.GetDefaultDownloadsFolder();

        _folderWatcher = new FolderWatcherService(_nexusIdDb, OnArchiveDetected, _settings.NexusAPIKey);
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
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            AddToInstallQueue(info);
            EnqueueInstallBatch();
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

        BeginScanning();
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
                var installResult = await Downloader.InstallLocalArchiveAsync(
                    info.ArchivePath, modsFolder, _settings.BackupBeforeUpdate, progress);
                var primaryDest   = installResult.PrimaryPath;
                var replacedName  = installResult.GetReplacedName(primaryDest);

                // Save Nexus fileId so future update checks use accurate fileId comparison
                if (info.NexusFileId > 0 && !string.IsNullOrEmpty(info.MetaUuid))
                    _fileIdStore.SetFileId(info.MetaUuid, info.NexusModId ?? 0,
                                           info.NexusFileId, info.NexusFileName);

                _historyStore.Add(new Models.DownloadHistoryEntry
                {
                    HistoryDownloadedAt        = DateTime.UtcNow,
                    HistoryModName             = info.ModName,
                    HistoryPlatformModName     = platformModName,
                    HistoryFromVersion         = fromVersion,
                    HistoryToVersion           = !string.IsNullOrEmpty(info.ModVersion) ? info.ModVersion : "—",
                    HistorySource              = finalSource,
                    HistoryPageUrl             = pageUrl,
                    HistorySuccess             = true,
                    HistoryPakFileName         = string.IsNullOrEmpty(primaryDest) ? null : System.IO.Path.GetFileName(primaryDest),
                    HistoryReplacedPakFileName = replacedName,
                });
            }

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
            EndScanning();
            StatusText    = "";
            ProgressValue = 0;
        }
    }

    /// <summary>Restarts folder watcher after settings are saved.</summary>
    public void RestartFolderWatcher() => InitFolderWatcher();

    // === Mod scanning ===

    private async Task RefreshModsAsync()
    {
        if (_scanRefCount > 0) return;

        var folder = !string.IsNullOrEmpty(_settings.ModsFolderPath)
            ? _settings.ModsFolderPath
            : PathDiscovery.GetDefaultModsFolder();

        Logger.Info($"RefreshMods: scanning folder — {folder}");

        if (!PathDiscovery.ModsFolderExists(folder))
        {
            Logger.Warn($"RefreshMods: mods folder not found — {folder}");
            _installedModsCount = 0;
            OnPropertyChanged(nameof(InstalledModsCountDisplay));
            return;
        }

        BeginScanning();
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

            _installedMods = await ModScanner.ScanAsync(folder, progress);

            // Populate NexusModId from DB for mods that don't have it yet
            var dbChanged = false;

            // Pre-pass: clear any cached NexusModIds that are now known-removed/hidden on Nexus.
            // Runs before the resolution loop so removed IDs are never re-assigned.
            foreach (var mod in _installedMods)
            {
                if (mod.NexusModId.HasValue && _nexusIdDb.IsRemovedOnNexus(mod.NexusModId.Value))
                {
                    Logger.Info($"NexusIdDatabase: clearing removed mod ID {mod.NexusModId} from {mod.PakFileName}");
                    mod.NexusModId = null;
                    dbChanged = true;
                }
            }

            foreach (var mod in _installedMods)
            {
                if (mod.NexusModId.HasValue) continue;
                // Single unambiguous entry → use it directly
                var entry = _nexusIdDb.LookupSingle(mod.PakFileName);
                if (entry != null)
                {
                    mod.NexusModId = entry.NexusModId;
                    dbChanged = true;
                    continue;
                }
                // Multiple entries but all share the same modId → safe to use for classification
                var sharedModId = _nexusIdDb.LookupUnambiguousModId(mod.PakFileName);
                if (sharedModId.HasValue)
                {
                    mod.NexusModId = sharedModId;
                    dbChanged = true;
                    continue;
                }
                // Ambiguous modIds: narrow by mod author (filters out noise entries such as
                // translations or unrelated mods that reuse the same pak filename)
                var authorModId = _nexusIdDb.LookupByPakAndAuthor(mod.PakFileName, mod.MetaAuthor);
                if (authorModId.HasValue)
                {
                    mod.NexusModId = authorModId;
                    dbChanged = true;
                    continue;
                }
                // Ambiguous modIds, author didn't match → try normalised module name
                // ("CompatibilityFramework" ≈ "Compatibility Framework")
                var modNameModId = _nexusIdDb.LookupByPakAndModName(mod.PakFileName, mod.MetaModuleName);
                if (modNameModId.HasValue)
                {
                    mod.NexusModId = modNameModId;
                    dbChanged = true;
                    continue;
                }
                // Ambiguous modIds: use fileIdStore if this mod was downloaded through the app
                var storedEntry = _fileIdStore.GetEntry(mod.MetaUuid);
                if (storedEntry != null && storedEntry.NexusModId != 0)
                {
                    mod.NexusModId = storedEntry.NexusModId;
                    dbChanged = true;
                }
            }
            // Cross-pak author correlation: if all of an author's already-resolved paks point
            // to exactly ONE nexus mod ID, and that ID appears among the ambiguous DB candidates
            // for an unresolved pak by the same author — use it.
            // This handles load-order-divider paks and similar cases where pak/author/modname
            // matching alone fails but a sibling pak by the same author is already resolved.
            var authorKnownIds = _installedMods
                .Where(m => m.NexusModId.HasValue &&
                            !string.IsNullOrWhiteSpace(m.MetaAuthor) &&
                            m.MetaAuthor != "—")
                .GroupBy(m => m.MetaAuthor!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(m => m.NexusModId!.Value).Distinct().ToList(),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var mod in _installedMods)
            {
                if (mod.NexusModId.HasValue) continue;
                if (string.IsNullOrWhiteSpace(mod.MetaAuthor) || mod.MetaAuthor == "—") continue;
                if (!authorKnownIds.TryGetValue(mod.MetaAuthor, out var knownIds)) continue;
                if (knownIds.Count != 1) continue; // only if author maps to exactly one known ID
                var knownId = knownIds[0];
                var candidates = _nexusIdDb.LookupByPakFileName(mod.PakFileName);
                if (candidates.Any(e => e.NexusModId == knownId))
                {
                    mod.NexusModId = knownId;
                    dbChanged = true;
                }
            }

            if (dbChanged) ModScanner.SaveNexusIds(_installedMods);

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
            EndScanning();
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
            // Add the new intruder to KnownHandlers before overwriting (never add Helper itself)
            if (!string.IsNullOrEmpty(current) &&
                !NxmHandler.IsSelfExe(NxmHandler.ExtractExePath(current)) &&
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

    private void BeginScanning()
    {
        if (System.Threading.Interlocked.Increment(ref _scanRefCount) == 1)
            Application.Current.Dispatcher.InvokeAsync(() => IsScanning = true);
    }

    private void EndScanning()
    {
        if (System.Threading.Interlocked.Decrement(ref _scanRefCount) <= 0)
        {
            System.Threading.Interlocked.Exchange(ref _scanRefCount, 0);
            Application.Current.Dispatcher.InvokeAsync(() => IsScanning = false);
        }
    }

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
            UnifiedDownloadQueue.Instance.ReportNxmProgress(item, p));

        var result      = await NxmInstaller.HandleAsync(item, progress);
        var errorReason = result is null ? NxmInstaller.ConsumeLastError() : null;

        // NxmInstaller runs as a separate instance that persists fileIdStore, so reload from disk
        if (result is not null) _fileIdStore.Load();

        UnifiedDownloadQueue.Instance.NotifyNxmCompleted(item, result is not null, errorReason);

        if (result is not null)
        {
            _ = Application.Current.Dispatcher.InvokeAsync(() =>
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
                .LookupSingle(result.PakFileName)
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
            _ = Application.Current.Dispatcher.InvokeAsync(() =>
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
            var failDbEntry      = failMod != null ? _nexusIdDb.LookupSingle(failMod.PakFileName) : null;
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
