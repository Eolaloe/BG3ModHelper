using System.Diagnostics;
using System.Windows;
using BG3ModHelper.Models;
using BG3ModHelper.Services;

namespace BG3ModHelper.ViewModels;

public partial class UpdateEntryViewModel : ViewModelBase
{
    private readonly ModUpdateEntry _entry;
    private readonly bool           _nexusIsPremium;
    private readonly string         _modsFolder;
    private readonly bool           _backupEnabled;
    private readonly ModioApi?       _modioApi;
    private readonly NexusApi?       _nexusApi;
    private readonly ModFileIdStore? _fileIdStore;
    private readonly DownloadHistoryStore? _historyStore;

    private bool                  _isSelected;
    private UpdateStatus          _status;
    private UpdateSource          _activeSource;
    private string                _nexusUrlInput    = "";
    private bool                  _isRegisterExpanded;
    private string                _statusText       = "";
    private DownloadInstallResult? _lastInstallResult;
    private CancellationTokenSource? _downloadCts;

    public UpdateEntryViewModel(
        ModUpdateEntry entry,
        bool nexusIsPremium,
        string modsFolder,
        bool backupEnabled           = false,
        ModioApi? modioApi           = null,
        NexusApi? nexusApi           = null,
        ModFileIdStore? fileIdStore  = null,
        DownloadHistoryStore? historyStore = null)
    {
        _entry          = entry;
        _nexusIsPremium = nexusIsPremium;
        _modsFolder     = modsFolder;
        _backupEnabled  = backupEnabled;
        _modioApi       = modioApi;
        _nexusApi       = nexusApi;
        _fileIdStore    = fileIdStore;
        _historyStore   = historyStore;
        _activeSource   = entry.DefaultSource;
        _status         = UpdateStatus.Pending;
        _isSelected     = entry.CanAutoDownload ||
                          (entry.AvailableSources.Contains(UpdateSource.NEXUSMODS) &&
                           entry.NexusModId != null);

        PrimaryActionCommand  = new RelayCommand(ExecutePrimaryAction, CanExecutePrimaryAction);
        SwitchSourceCommand   = new RelayCommand<string>(SwitchSource);
        ToggleSourceCommand   = new RelayCommand(ToggleSource, () => HasMultipleSources);
        ToggleExpandCommand   = new RelayCommand(() => IsGroupExpanded = !IsGroupExpanded, () => HasGroupChildren);
        OpenPageCommand       = new RelayCommand(OpenPage, () => !string.IsNullOrEmpty(ActivePageUrl));
        RegisterNexusCommand  = new RelayCommand(RegisterNexus, () => !string.IsNullOrEmpty(_nexusUrlInput.Trim()));
        TogglePreserveCommand = new RelayCommand(TogglePreserve);
    }

    // === Identity ===
    public string UUID           => _entry.MetaUuid;
    public int?   NexusModId     => _entry.NexusModId;
    public string ModName        => _entry.UpdateModName;

    // Two-line name display: platform name (top) + MetaModuleName (bottom)
    public string PlatformModName =>
        ActiveSource == UpdateSource.MODIO ? _entry.ModioModName : _entry.NexusModName;

    public string LocalModName => _entry.UpdateModName;

    public bool HasPlatformName => !string.IsNullOrEmpty(PlatformModName);

    // Sort keys for ICollectionView SortDescriptions
    public string SortName => !string.IsNullOrEmpty(PlatformModName) ? PlatformModName : LocalModName;
    /// <summary>
    /// Sort key matching InstalledMods badge order:
    /// Nexus only=0, mod.io only=1, Both=2, NexusUnregistered/Others=3
    /// Uses source presence (not active download source) so "Both" mods group together
    /// regardless of which source is currently selected.
    /// </summary>
    public int SortSourceOrder =>
        HasNexus &&  HasModio              ? 2 :
        HasNexus && !HasModio              ? (IsNexusUnregistered ? 3 : 0) :
        HasModio && !HasNexus              ? 1 : 3;
    /// <summary>0 = preserved (locked), 1 = normal. Used for the Version column sort.</summary>
    public int SortPreserveOrder => IsPreserved ? 0 : 1;

    public string CurrentVersion => _entry.UpdateCurrentVersion;
    public string NewVersion     => _entry.UpdateNewVersion;
    public bool   RequiresManualCheck => _entry.RequiresManualCheck;
    public long   NexusFileId          => _entry.NexusFileId;
    public ulong  ModioPublishHandle   => _entry.ModioPublishHandle;
    public string PakFilePath          => _entry.PakFilePath ?? "";

    // === Group support ===
    private readonly List<UpdateEntryViewModel> _groupChildren = [];

    public IReadOnlyList<UpdateEntryViewModel> GroupChildren    => _groupChildren;
    public bool                                HasGroupChildren => _groupChildren.Count > 0;

    private bool _isGroupExpanded;
    public bool IsGroupExpanded
    {
        get => _isGroupExpanded;
        set
        {
            SetField(ref _isGroupExpanded, value);
            OnPropertyChanged(nameof(ExpandIcon));
        }
    }

    public string ExpandIcon => _isGroupExpanded ? "▼" : "▶";

    internal void AddGroupChild(UpdateEntryViewModel child)
    {
        _groupChildren.Add(child);
        OnPropertyChanged(nameof(GroupChildren));
        OnPropertyChanged(nameof(HasGroupChildren));
    }

    /// <summary>
    /// Removes Nexus as an available source. Called when a mod is detected inside
    /// a bundle reupload (incoherent pak group) — the Nexus attribution is noise,
    /// not the mod's own page.
    /// </summary>
    internal void StripNexusSource()
    {
        _entry.AvailableSources.Remove(UpdateSource.NEXUSMODS);
        _entry.NexusModPageUrl  = "";
        _entry.NexusFileVersion = "";
        _entry.NexusModName     = "";
        _entry.NexusFileId      = 0;
        _entry.NexusModId       = null;

        _entry.DefaultSource   = UpdateSource.MODIO;
        _entry.CanAutoDownload = _entry.AvailableSources.Contains(UpdateSource.MODIO);

        _activeSource = _entry.DefaultSource;

        OnPropertyChanged(nameof(ActiveSource));
        OnPropertyChanged(nameof(HasNexus));
        OnPropertyChanged(nameof(HasModio));
        OnPropertyChanged(nameof(HasMultipleSources));
        OnPropertyChanged(nameof(ShowSwitchButtons));
        OnPropertyChanged(nameof(CanSwitchToModio));
        OnPropertyChanged(nameof(CanSwitchToNexus));
        OnPropertyChanged(nameof(CanAutoDownload));
        OnPropertyChanged(nameof(CanBeQueued));
        OnPropertyChanged(nameof(IsNexusUnregistered));
        OnPropertyChanged(nameof(SourceBadge));
        OnPropertyChanged(nameof(SourceBadgeColor));
        OnPropertyChanged(nameof(BackSourceBadge));
        OnPropertyChanged(nameof(BackSourceBadgeColor));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(ActionStyle));
        OnPropertyChanged(nameof(ActivePageUrl));
        OnPropertyChanged(nameof(NexusModId));
        OnPropertyChanged(nameof(SortSourceOrder));
        OnPropertyChanged(nameof(PlatformModName));
        OnPropertyChanged(nameof(HasPlatformName));
        PrimaryActionCommand.RaiseCanExecuteChanged();
    }

    public string Changelog =>
        IsSyncRequired
            ? "Version scheme mismatch detected — the installed file's internal version differs from the Nexus listing. Re-downloading once will store the file ID for accurate update tracking."
            : _entry.RequiresManualCheck
                ? (!string.IsNullOrEmpty(_entry.Changelog)
                    ? _entry.Changelog
                    : "File mapping uncertain — please verify the correct file on the mod page.")
                : ActiveSource == UpdateSource.MODIO ? _entry.ModioChangelog : _entry.Changelog;

    public string VersionDisplay =>
        string.IsNullOrEmpty(NewVersion)
            ? $"v{CurrentVersion} → ?"
            : $"v{CurrentVersion} → v{NewVersion}";

    /// <summary>Version for the current ActiveSource — auto-updated on source switch.</summary>
    public string NewVersionDisplay
    {
        get
        {
            var v = ActiveSource == UpdateSource.MODIO
                ? _entry.ModioFileVersion
                : _entry.NexusFileVersion;
            return string.IsNullOrEmpty(v) ? _entry.UpdateNewVersion : v;
        }
    }

    // === Source ===
    public UpdateSource ActiveSource
    {
        get => _activeSource;
        private set
        {
            SetField(ref _activeSource, value);
            OnPropertyChanged(nameof(SourceBadge));
            OnPropertyChanged(nameof(SourceBadgeColor));
            OnPropertyChanged(nameof(BackSourceBadge));
            OnPropertyChanged(nameof(BackSourceBadgeColor));
            OnPropertyChanged(nameof(ActionLabel));
            OnPropertyChanged(nameof(ActionStyle));
            OnPropertyChanged(nameof(CanAutoDownload));
            OnPropertyChanged(nameof(CanBeQueued));
            OnPropertyChanged(nameof(HasMultipleSources));
            OnPropertyChanged(nameof(ShowSwitchButtons));
            OnPropertyChanged(nameof(ActivePageUrl));
            OnPropertyChanged(nameof(NewVersionDisplay));  // update version label on source switch
            OnPropertyChanged(nameof(PlatformModName));
            OnPropertyChanged(nameof(HasPlatformName));
            OnPropertyChanged(nameof(Changelog));
            PrimaryActionCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasModio       => _entry.AvailableSources.Contains(UpdateSource.MODIO);
    public bool HasNexus       => _entry.AvailableSources.Contains(UpdateSource.NEXUSMODS);
    public bool IsActive       => _entry.IsActive;
    public bool IsSyncRequired => _entry.IsSyncRequired;
    public bool IsNexusUnregistered =>
        !HasModio && !HasNexus && _entry.NexusModId == null;

    public bool HasMultipleSources => HasModio && HasNexus;
    public bool ShowSwitchButtons  => HasMultipleSources;
    public bool CanSwitchToModio   => HasMultipleSources && ActiveSource != UpdateSource.MODIO;
    public bool CanSwitchToNexus   => HasMultipleSources && ActiveSource != UpdateSource.NEXUSMODS;

    public string SourceBadge =>
        IsNexusUnregistered ? "Unlinked" :
        ActiveSource == UpdateSource.MODIO ? "mod.io" : "Nexus";

    public string SourceBadgeColor =>
        IsNexusUnregistered ? "#888888" :
        ActiveSource == UpdateSource.MODIO ? "#1a7fd4" : "#d98200";

    // ── Back badge (inactive source) — shown only when HasMultipleSources ────
    public string BackSourceBadge  =>
        ActiveSource == UpdateSource.MODIO ? "Nexus" : "mod.io";
    public string BackSourceBadgeColor =>
        ActiveSource == UpdateSource.MODIO ? "#d98200" : "#1a7fd4";

    public bool CanAutoDownload =>
        !IsNexusUnregistered &&
        !_entry.RequiresManualCheck &&
        (ActiveSource == UpdateSource.MODIO ||
         (ActiveSource == UpdateSource.NEXUSMODS && _nexusIsPremium));

    // True for auto-download AND Nexus Free items (both can be queued via Download Selected)
    public bool CanBeQueued => !IsPreserved && (CanAutoDownload || (HasNexus && !IsNexusUnregistered));

    // === Preserve (version lock) ===

    /// <summary>
    /// Whether this mod is locked at its current version.
    /// Preserved mods appear in the update list but are greyed out and excluded
    /// from batch downloads.
    /// </summary>
    public bool IsPreserved => Services.PreservedModsStore.Instance.IsPreserved(UUID);

    /// <summary>Lock icon color — amber when locked, light grey when unlocked.</summary>
    public string PreserveLockColor => IsPreserved ? "#d98200" : "#d0d0d0";

    public string PreserveLockTooltip => IsPreserved
        ? "Unlock version"
        : "Lock version  (ignore updates)";

    public RelayCommand TogglePreserveCommand { get; }

    private void TogglePreserve()
    {
        Services.PreservedModsStore.Instance.Toggle(UUID);
        if (IsPreserved)
            _isSelected = false;        // lock → deselect
        else if (CanBeQueued && IsActive)
            _isSelected = true;         // unlock → re-select (active only; inactive stays deselected by default)
        OnPropertyChanged(nameof(IsPreserved));
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(CanBeQueued));
        OnPropertyChanged(nameof(IsActionEnabled));
        OnPropertyChanged(nameof(IsActionButtonEnabled));
        OnPropertyChanged(nameof(PreserveLockColor));
        OnPropertyChanged(nameof(PreserveLockTooltip));
        OnPropertyChanged(nameof(SortPreserveOrder));
        PrimaryActionCommand.RaiseCanExecuteChanged();
    }

    private bool IsDownloadingOrApplying =>
        Status == UpdateStatus.Downloading || Status == UpdateStatus.Applying;

    public string ActionLabel
    {
        get
        {
            if (IsNexusUnregistered)          return "Link Nexus";
            if (Status == UpdateStatus.Retry) return "Retry";
            if (IsDownloadingOrApplying)      return "■ Stop";
            if (CanAutoDownload)              return IsSyncRequired ? "Re-download" : "Download";
            return "Open Page";
        }
    }

    public string ActionStyle =>
        IsNexusUnregistered     ? "RegisterActionButton" :
        IsDownloadingOrApplying ? "StopActionButton"     :
        CanAutoDownload         ? "PrimaryActionButton"  : "SecondaryActionButton";

    /// <summary>
    /// Whether the action button row is clickable.
    /// True for normal actionable states AND during download (for cancel).
    /// </summary>
    public bool IsActionButtonEnabled => IsActionEnabled || IsDownloadingOrApplying;

    public string ActivePageUrl =>
        ActiveSource == UpdateSource.MODIO ? _entry.ModioProfileUrl : _entry.NexusModPageUrl;

    // === Selection ===
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    // === Status ===
    public UpdateStatus Status
    {
        get => _status;
        set
        {
            SetField(ref _status, value);
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(ActionLabel));
            OnPropertyChanged(nameof(ActionStyle));
            OnPropertyChanged(nameof(IsActionEnabled));
            OnPropertyChanged(nameof(IsActionButtonEnabled));
            PrimaryActionCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    /// <summary>
    /// Size progress shown on the second line during download ("X.X MB / Y.Y MB").
    /// Empty when not downloading or when file size is unknown.
    /// </summary>
    private string _statusProgress = "";
    public string StatusProgress
    {
        get => _statusProgress;
        set
        {
            if (SetField(ref _statusProgress, value))
                OnPropertyChanged(nameof(HasStatusProgress));
        }
    }
    public bool HasStatusProgress => !string.IsNullOrEmpty(_statusProgress);

    public string StatusColor => Status switch
    {
        UpdateStatus.Updated     => "#4caf50",
        UpdateStatus.Failed      => "#f44336",
        UpdateStatus.Retry       => "#ff9800",
        UpdateStatus.Skipped     => "#888888",
        UpdateStatus.Downloading => "#0078d4",
        UpdateStatus.Applying    => "#ff9800",
        _                        => "#cccccc"
    };

    public bool IsActionEnabled =>
        !IsPreserved &&
        (Status == UpdateStatus.Pending  ||
         Status == UpdateStatus.Failed   ||
         Status == UpdateStatus.Retry    ||
         Status == UpdateStatus.Skipped); // Cancelled items can be re-downloaded

    // === Nexus link panel ===
    public bool ShowNexusRegisterPanel => IsNexusUnregistered && _isRegisterExpanded;

    public string NexusUrlInput
    {
        get => _nexusUrlInput;
        set
        {
            SetField(ref _nexusUrlInput, value);
            RegisterNexusCommand.RaiseCanExecuteChanged();
        }
    }

    // === Commands ===
    public RelayCommand         PrimaryActionCommand { get; }
    public RelayCommand<string> SwitchSourceCommand  { get; }
    public RelayCommand         ToggleSourceCommand  { get; }
    public RelayCommand         ToggleExpandCommand  { get; }
    public RelayCommand         OpenPageCommand       { get; }
    public RelayCommand         RegisterNexusCommand  { get; }

    public event Action<UpdateEntryViewModel>?      DownloadRequested;
    public event Action<UpdateEntryViewModel>?      PageOpenRequested;
    public event Action<UpdateEntryViewModel, int>? NexusRegistered;
    public event Action<UpdateEntryViewModel>?      CancelRequested;

    // === Download ===

    /// <summary>Called by parent ViewModel to start the actual download.</summary>
    /// <param name="onProgress">Optional callback fired on each progress update (e.g. to forward to the main window).</param>
    public async Task ExecuteDownloadAsync(Action<DownloadProgress>? onProgress = null)
    {
        if (!CanAutoDownload || !IsActionEnabled) return;
        // Prevent re-entry while already running
        if (IsDownloadingOrApplying) return;

        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;

        Status         = UpdateStatus.Downloading;
        StatusText     = "Download";
        StatusProgress = "";

        try
        {
            string downloadUrl;
            long   nexusFileId  = 0;
            string nexusFileName = "";

            if (ActiveSource == UpdateSource.MODIO)
            {
                downloadUrl = await GetModioDownloadUrlAsync();
            }
            else
            {
                (downloadUrl, nexusFileId, nexusFileName) = await GetNexusDownloadUrlAsync();
            }

            if (string.IsNullOrEmpty(downloadUrl))
                throw new InvalidOperationException("Could not obtain download URL.");

            var progress = new Progress<DownloadProgress>(p =>
            {
                if (p.Text.StartsWith("Extracting") || p.Text.StartsWith("Applying") || p.Text.StartsWith("Backing"))
                {
                    // Phase transition: clear size progress, show step label
                    Status         = UpdateStatus.Applying;
                    StatusText     = p.Text switch
                    {
                        "Extracting" => "Extract",
                        "Applying"   => "Apply",
                        "Backing up" => "Backup",
                        _            => p.Text
                    };
                    StatusProgress = "";
                }
                else if (Status == UpdateStatus.Downloading)
                {
                    // Byte progress: "X.X MB / Y.Y MB" — show on second line
                    StatusProgress = p.Text;
                }
                else
                {
                    StatusText = p.Text;
                }
                onProgress?.Invoke(p);
            });

            if (HasGroupChildren)
            {
                var targets = new List<PakInstallTarget>
                {
                    new(_entry.PakFilePath ?? "", _entry.MetaUuid, _entry.NexusModId ?? 0,
                        nexusFileId, nexusFileName)
                };
                foreach (var child in _groupChildren)
                    targets.Add(new(child._entry.PakFilePath ?? "", child._entry.MetaUuid,
                        child._entry.NexusModId ?? 0, nexusFileId, nexusFileName));

                await Downloader.DownloadAndInstallGroupAsync(
                    downloadUrl, targets, _modsFolder, _backupEnabled,
                    ActiveSource == UpdateSource.NEXUSMODS ? _fileIdStore : null,
                    progress, ct);

                foreach (var child in _groupChildren)
                {
                    child.Status     = UpdateStatus.Updated;
                    child.StatusText = "Updated";
                }
            }
            else
            {
                var result = await Downloader.DownloadAndInstallAsync(
                    downloadUrl,
                    _entry.PakFilePath ?? "",
                    _modsFolder,
                    _backupEnabled,
                    uuid:        _entry.MetaUuid,
                    modId:       _entry.NexusModId ?? 0,
                    fileId:      nexusFileId,
                    fileName:    nexusFileName,
                    fileIdStore: ActiveSource == UpdateSource.NEXUSMODS ? _fileIdStore : null,
                    progress:    progress,
                    ct:          ct);
                _lastInstallResult = result;
            }

            Status         = UpdateStatus.Updated;
            StatusText     = "Updated";
            StatusProgress = "";
            Logger.Info($"Download complete: {ModName}");
            RecordHistory(success: true);
        }
        catch (PakInUseException ex)
        {
            Status     = UpdateStatus.Retry;
            StatusText = "Retry";
            foreach (var child in _groupChildren) { child.Status = UpdateStatus.Retry; child.StatusText = "Retry"; }
            Logger.Warn($"File in use for {ModName}: {ex.Message}");

            _ = Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(
                    $"Could not overwrite {ex.PakFileName}.\nClose BG3 and click Retry.",
                    "File In Use",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning));
        }
        catch (ManagerDownloadDisabledException ex)
        {
            // Manager downloads disabled by mod author — route to WebView manual download queue
            Status     = UpdateStatus.ManualRequired;
            StatusText = "Manual";
            foreach (var child in _groupChildren) { child.Status = UpdateStatus.ManualRequired; child.StatusText = "Manual"; }
            Logger.Warn($"Manager download disabled for {ModName} — queuing for manual download: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            Status     = UpdateStatus.Skipped;
            StatusText = "Cancelled";
            Logger.Info($"Download cancelled: {ModName}");
        }
        catch (Exception ex)
        {
            Status     = UpdateStatus.Failed;
            StatusText = "Failed";
            foreach (var child in _groupChildren) { child.Status = UpdateStatus.Failed; child.StatusText = "Failed"; }
            Logger.Error($"Download failed for {ModName}: {ex.Message}");
            RecordHistory(success: false);

            _ = Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(
                    $"Download failed for {ModName}:\n{ex.Message}",
                    "Download Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error));
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    /// <summary>Immediately cancels the active download (if any).</summary>
    internal void CancelDownloadNow() => _downloadCts?.Cancel();

    private async Task<string> GetModioDownloadUrlAsync()
    {
        if (_modioApi == null)
            throw new InvalidOperationException("mod.io API not initialized.");

        var file = await _modioApi.GetLatestFileAsync(_entry.ModioPublishHandle)
            ?? throw new InvalidOperationException($"No file found for mod.io mod {_entry.ModioPublishHandle}.");

        Logger.Info($"mod.io download URL obtained: {file.ModioFileName}");
        return file.ModioBinaryUrl;
    }

    private async Task<(string url, long fileId, string fileName)> GetNexusDownloadUrlAsync()
    {
        if (_nexusApi == null || _entry.NexusModId == null)
            throw new InvalidOperationException("Nexus API key is not configured, or this mod has no Nexus ID.");

        // Use the specific fileId stored in the update entry (set by UpdateChecker from the DB).
        // This is critical for mods that ship multiple variants under one modId (e.g. "Better Map
        // 0.85 scale" vs "Better Map 1.2 scale") — GetLatestFileAsync would return the highest fileId
        // regardless of which variant the user has installed, causing the wrong file to be downloaded.
        // Fall back to GetLatestFileAsync only when no fileId is recorded (rare: API-fallback path).
        long   targetFileId   = _entry.NexusFileId;
        string targetFileName = _entry.NexusFileName;

        if (targetFileId == 0)
        {
            var latest = await _nexusApi.GetLatestFileAsync(_entry.NexusModId.Value)
                ?? throw new InvalidOperationException(
                    $"Could not retrieve file list for Nexus mod {_entry.NexusModId}.\n" +
                    "(Check logs for the exact server response — 403/429/404)");
            targetFileId   = latest.NexusFileId;
            targetFileName = latest.NexusFileName;
        }

        // Premium download URL (download_link.json)
        var url = await _nexusApi.GetDownloadUrlAsync(_entry.NexusModId.Value, targetFileId);
        if (string.IsNullOrEmpty(url))
        {
            // 403 on download_link for a premium account → manager downloads disabled by mod author
            if (_nexusApi.LastStatusCode == 403)
                throw new ManagerDownloadDisabledException(
                    $"Manager downloads are disabled for mod {_entry.NexusModId} (file {targetFileId}).");

            throw new InvalidOperationException(
                $"Nexus did not return a download URL for mod {_entry.NexusModId} (file {targetFileId}).\n" +
                "(If this persists, verify that your API key has Premium access.)");
        }

        return (url, targetFileId, targetFileName);
    }

    // === Action dispatch ===

    private void ExecutePrimaryAction()
    {
        if (IsNexusUnregistered)
        {
            _isRegisterExpanded = !_isRegisterExpanded;
            OnPropertyChanged(nameof(ShowNexusRegisterPanel));
            return;
        }

        if (IsDownloadingOrApplying)
        {
            CancelRequested?.Invoke(this);
            return;
        }

        if (!CanAutoDownload)
        {
            PageOpenRequested?.Invoke(this);
            return;
        }

        DownloadRequested?.Invoke(this);
    }

    private bool CanExecutePrimaryAction() =>
        IsNexusUnregistered || IsActionEnabled || IsDownloadingOrApplying;

    private void ToggleSource()
    {
        if (ActiveSource == UpdateSource.MODIO && HasNexus)
            SwitchSource("nexus");
        else if (ActiveSource == UpdateSource.NEXUSMODS && HasModio)
            SwitchSource("modio");
    }

    private void SwitchSource(string? source)
    {
        if (source == "modio" && HasModio)
        {
            _entry.PreferredSource = UpdateSource.MODIO;
            ActiveSource = UpdateSource.MODIO;
        }
        else if (source == "nexus" && HasNexus)
        {
            _entry.PreferredSource = UpdateSource.NEXUSMODS;
            ActiveSource = UpdateSource.NEXUSMODS;
        }
    }

    private void OpenPage()
    {
        var url = ActivePageUrl;
        if (!string.IsNullOrEmpty(url))
        {
            if (!CanAutoDownload && ActiveSource == UpdateSource.NEXUSMODS)
                url = url.Contains('?') ? $"{url}&tab=files" : $"{url}?tab=files";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    private void RecordHistory(bool success)
    {
        if (_historyStore == null) return;

        var source = ActiveSource switch
        {
            UpdateSource.MODIO     => "ModIO",
            UpdateSource.NEXUSMODS => "Nexus",
            _                      => "Others"
        };

        var pageUrl = ActiveSource switch
        {
            UpdateSource.NEXUSMODS when _entry.NexusModId.HasValue =>
                $"https://www.nexusmods.com/baldursgate3/mods/{_entry.NexusModId.Value}",
            UpdateSource.MODIO when !string.IsNullOrEmpty(_entry.ModioProfileUrl) =>
                _entry.ModioProfileUrl,
            _ => null
        };

        var groupId = HasGroupChildren ? Guid.NewGuid().ToString("N") : null;
        var now     = DateTime.UtcNow;

        var primaryDest = _lastInstallResult?.PrimaryPath ?? "";
        _historyStore.Add(new DownloadHistoryEntry
        {
            HistoryDownloadedAt        = now,
            HistoryModName             = ModName,
            HistoryPlatformModName     = PlatformModName,
            HistoryFromVersion         = string.IsNullOrEmpty(CurrentVersion) ? "Not installed" : CurrentVersion,
            HistoryToVersion           = NewVersionDisplay,
            HistorySource              = source,
            HistoryPageUrl             = pageUrl,
            HistorySuccess             = success,
            HistoryGroupId             = groupId,
            HistoryPakFileName         = string.IsNullOrEmpty(primaryDest) ? null : System.IO.Path.GetFileName(primaryDest),
            HistoryReplacedPakFileName = _lastInstallResult?.GetReplacedName(primaryDest),
        });

        foreach (var child in _groupChildren)
        {
            _historyStore.Add(new DownloadHistoryEntry
            {
                HistoryDownloadedAt    = now,
                HistoryModName         = child.ModName,
                HistoryPlatformModName = child.PlatformModName,
                HistoryFromVersion     = string.IsNullOrEmpty(child.CurrentVersion) ? "Not installed" : child.CurrentVersion,
                HistoryToVersion       = child.NewVersionDisplay,
                HistorySource          = source,
                HistoryPageUrl         = pageUrl,
                HistorySuccess         = success,
                HistoryGroupId         = groupId,
            });
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"nexusmods\.com/[^/]+/mods/(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex NexusUrlRegex();

    private void RegisterNexus()
    {
        var input = _nexusUrlInput.Trim();
        // Parse modId from URL: nexusmods.com/baldursgate3/mods/{modId}
        var match = NexusUrlRegex().Match(input);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var modId))
        {
            MessageBox.Show("Please enter a valid Nexus Mods URL.\nExample: https://www.nexusmods.com/baldursgate3/mods/12345",
                "Invalid URL", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _entry.NexusModId = modId;
        NexusRegistered?.Invoke(this, modId);
        _nexusUrlInput      = "";
        _isRegisterExpanded = false;
        OnPropertyChanged(nameof(NexusUrlInput));
        OnPropertyChanged(nameof(ShowNexusRegisterPanel));
        OnPropertyChanged(nameof(IsNexusUnregistered));
        OnPropertyChanged(nameof(HasNexus));
        OnPropertyChanged(nameof(CanAutoDownload));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(ActionStyle));
        PrimaryActionCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>
/// Thrown when Nexus returns HTTP 403 on download_link.json for a premium account,
/// indicating the mod author has disabled manager downloads for this file.
/// The caller should route this entry to the WebView manual download queue.
/// </summary>
public sealed class ManagerDownloadDisabledException(string message) : Exception(message) { }
