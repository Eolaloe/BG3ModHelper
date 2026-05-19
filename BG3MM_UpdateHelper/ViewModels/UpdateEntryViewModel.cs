using System.Diagnostics;
using System.Windows;
using BG3MM_UpdateHelper.Models;
using BG3MM_UpdateHelper.Services;

namespace BG3MM_UpdateHelper.ViewModels;

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

    private bool         _isSelected;
    private UpdateStatus _status;
    private UpdateSource _activeSource;
    private string       _nexusUrlInput    = "";
    private bool         _isRegisterExpanded;
    private string       _statusText       = "";

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
        _isSelected     = entry.CanAutoDownload;

        PrimaryActionCommand = new RelayCommand(ExecutePrimaryAction, CanExecutePrimaryAction);
        SwitchSourceCommand  = new RelayCommand<string>(SwitchSource);
        ToggleSourceCommand  = new RelayCommand(ToggleSource, () => HasMultipleSources);
        OpenPageCommand      = new RelayCommand(OpenPage, () => !string.IsNullOrEmpty(ActivePageUrl));
        RegisterNexusCommand = new RelayCommand(RegisterNexus, () => !string.IsNullOrEmpty(_nexusUrlInput.Trim()));
    }

    // === Identity ===
    public string UUID           => _entry.MetaUuid;
    public string ModName        => _entry.UpdateModName;
    public string CurrentVersion => _entry.UpdateCurrentVersion;
    public string NewVersion     => _entry.UpdateNewVersion;

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
            OnPropertyChanged(nameof(ActionLabel));
            OnPropertyChanged(nameof(ActionStyle));
            OnPropertyChanged(nameof(CanAutoDownload));
            OnPropertyChanged(nameof(HasMultipleSources));
            OnPropertyChanged(nameof(ShowSwitchButtons));
            OnPropertyChanged(nameof(ActivePageUrl));
            OnPropertyChanged(nameof(NewVersionDisplay));  // update version label on source switch
            PrimaryActionCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasModio => _entry.AvailableSources.Contains(UpdateSource.MODIO);
    public bool HasNexus => _entry.AvailableSources.Contains(UpdateSource.NEXUSMODS);
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

    public bool CanAutoDownload =>
        !IsNexusUnregistered &&
        (ActiveSource == UpdateSource.MODIO ||
         (ActiveSource == UpdateSource.NEXUSMODS && _nexusIsPremium));

    public string ActionLabel
    {
        get
        {
            if (IsNexusUnregistered)                                      return "Link Nexus";
            if (Status == UpdateStatus.Retry)                             return "Retry";
            if (Status == UpdateStatus.Downloading ||
                Status == UpdateStatus.Applying)                          return "...";
            if (CanAutoDownload)                                          return "Download";
            return "Open Page";
        }
    }

    public string ActionStyle =>
        IsNexusUnregistered ? "RegisterActionButton" :
        CanAutoDownload      ? "PrimaryActionButton"  : "SecondaryActionButton";

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
            OnPropertyChanged(nameof(IsActionEnabled));
            PrimaryActionCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

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
        Status == UpdateStatus.Pending ||
        Status == UpdateStatus.Failed  ||
        Status == UpdateStatus.Retry;

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
    public RelayCommand         OpenPageCommand       { get; }
    public RelayCommand         RegisterNexusCommand  { get; }

    public event Action<UpdateEntryViewModel>?      DownloadRequested;
    public event Action<UpdateEntryViewModel, int>? NexusRegistered;

    // === Download ===

    /// <summary>Called by parent ViewModel to start the actual download.</summary>
    public async Task ExecuteDownloadAsync()
    {
        if (!CanAutoDownload || !IsActionEnabled) return;

        Status     = UpdateStatus.Downloading;
        StatusText = "Downloading...";

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
                StatusText = p.Text;
                if (p.Percent >= 95)
                    Status = UpdateStatus.Applying;
            });

            await Downloader.DownloadAndInstallAsync(
                downloadUrl,
                _entry.PakFilePath ?? "",
                _modsFolder,
                _backupEnabled,
                uuid:        _entry.MetaUuid,
                modId:       _entry.NexusModId ?? 0,
                fileId:      nexusFileId,
                fileName:    nexusFileName,
                fileIdStore: ActiveSource == UpdateSource.NEXUSMODS ? _fileIdStore : null,
                progress:    progress);

            Status     = UpdateStatus.Updated;
            StatusText = "Updated";
            Logger.Info($"Download complete: {ModName}");
            RecordHistory(success: true);
        }
        catch (PakInUseException ex)
        {
            Status     = UpdateStatus.Retry;
            StatusText = "Retry";
            Logger.Warn($"File in use for {ModName}: {ex.Message}");

            _ = Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(
                    $"Could not overwrite {ex.PakFileName}.\nClose BG3 and click Retry.",
                    "File In Use",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning));
        }
        catch (OperationCanceledException)
        {
            Status     = UpdateStatus.Skipped;
            StatusText = "Cancelled";
        }
        catch (Exception ex)
        {
            Status     = UpdateStatus.Failed;
            StatusText = "Failed";
            Logger.Error($"Download failed for {ModName}: {ex.Message}");
            RecordHistory(success: false);

            _ = Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(
                    $"Download failed for {ModName}:\n{ex.Message}",
                    "Download Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error));
        }
    }

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

        // Step 1: latest file metadata (get fileId)
        var latest = await _nexusApi.GetLatestFileAsync(_entry.NexusModId.Value)
            ?? throw new InvalidOperationException(
                $"Could not retrieve file list for Nexus mod {_entry.NexusModId}.\n" +
                "(Check logs for the exact server response — 403/429/404)");

        // Step 2: Premium download URL (download_link.json)
        var url = await _nexusApi.GetDownloadUrlAsync(_entry.NexusModId.Value, latest.NexusFileId);
        if (string.IsNullOrEmpty(url))
            throw new InvalidOperationException(
                $"Nexus did not return a download URL for mod {_entry.NexusModId} (file {latest.NexusFileId}).\n" +
                "(If this persists, verify that your API key has Premium access.)");

        return (url, latest.NexusFileId, latest.NexusFileName);
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

        if (!CanAutoDownload)
        {
            OpenPage();
            return;
        }

        DownloadRequested?.Invoke(this);
    }

    private bool CanExecutePrimaryAction() =>
        IsNexusUnregistered ||
        (IsActionEnabled &&
         Status != UpdateStatus.Downloading &&
         Status != UpdateStatus.Applying);

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

        _historyStore.Add(new DownloadHistoryEntry
        {
            HistoryDownloadedAt = DateTime.UtcNow,
            HistoryModName      = ModName,
            HistoryFromVersion  = string.IsNullOrEmpty(CurrentVersion) ? "Not installed" : CurrentVersion,
            HistoryToVersion    = NewVersionDisplay,
            HistorySource       = source,
            HistoryPageUrl      = pageUrl,
            HistorySuccess      = success,
        });
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
