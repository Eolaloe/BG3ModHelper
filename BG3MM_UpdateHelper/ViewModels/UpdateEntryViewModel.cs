using System.Diagnostics;
using System.Windows;
using BG3MM_UpdateHelper.Models;
using BG3MM_UpdateHelper.Services;

namespace BG3MM_UpdateHelper.ViewModels;

public class UpdateEntryViewModel : ViewModelBase
{
    private readonly ModUpdateEntry _entry;
    private readonly bool           _nexusIsPremium;
    private readonly string         _modsFolder;
    private readonly bool           _backupEnabled;
    private readonly ModioApi?      _modioApi;
    private readonly NexusApi?      _nexusApi;

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
        bool backupEnabled  = false,
        ModioApi? modioApi  = null,
        NexusApi? nexusApi  = null)
    {
        _entry          = entry;
        _nexusIsPremium = nexusIsPremium;
        _modsFolder     = modsFolder;
        _backupEnabled  = backupEnabled;
        _modioApi       = modioApi;
        _nexusApi       = nexusApi;
        _activeSource   = entry.DefaultSource;
        _status         = UpdateStatus.Pending;
        _isSelected     = entry.CanAutoDownload;

        PrimaryActionCommand = new RelayCommand(ExecutePrimaryAction, CanExecutePrimaryAction);
        SwitchSourceCommand  = new RelayCommand<string>(SwitchSource);
        OpenPageCommand      = new RelayCommand(OpenPage, () => !string.IsNullOrEmpty(ActivePageUrl));
        RegisterNexusCommand = new RelayCommand(RegisterNexus, () => !string.IsNullOrEmpty(_nexusUrlInput.Trim()));
    }

    // ── Identity ──────────────────────────────────────────────────────────
    public string UUID           => _entry.UUID;
    public string ModName        => _entry.ModName;
    public string CurrentVersion => _entry.CurrentVersion;
    public string NewVersion     => _entry.NewVersion;

    public string VersionDisplay =>
        string.IsNullOrEmpty(NewVersion)
            ? $"v{CurrentVersion} → ?"
            : $"v{CurrentVersion} → v{NewVersion}";

    // ── Source ────────────────────────────────────────────────────────────
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
            if (IsNexusUnregistered)         return "Link Nexus";
            if (Status == UpdateStatus.Done) return "Done";
            if (Status == UpdateStatus.Downloading || Status == UpdateStatus.Installing)
                return "...";
            if (CanAutoDownload) return "Download";
            return "Open Page";
        }
    }

    public string ActionStyle =>
        IsNexusUnregistered ? "RegisterActionButton" :
        CanAutoDownload      ? "PrimaryActionButton"  : "SecondaryActionButton";

    public string ActivePageUrl =>
        ActiveSource == UpdateSource.MODIO ? _entry.ModioUrl : _entry.NexusUrl;

    // ── Selection ─────────────────────────────────────────────────────────
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    // ── Status ────────────────────────────────────────────────────────────
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
        UpdateStatus.Done        => "#4caf50",
        UpdateStatus.Failed      => "#f44336",
        UpdateStatus.Skipped     => "#888888",
        UpdateStatus.Downloading => "#0078d4",
        UpdateStatus.Installing  => "#ff9800",
        _                        => "#cccccc"
    };

    public bool IsActionEnabled =>
        Status == UpdateStatus.Pending || Status == UpdateStatus.Failed;

    // ── Nexus link panel ──────────────────────────────────────────────────
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

    // ── Commands ──────────────────────────────────────────────────────────
    public RelayCommand         PrimaryActionCommand { get; }
    public RelayCommand<string> SwitchSourceCommand  { get; }
    public RelayCommand         OpenPageCommand       { get; }
    public RelayCommand         RegisterNexusCommand  { get; }

    public event Action<UpdateEntryViewModel>?      DownloadRequested;
    public event Action<UpdateEntryViewModel, int>? NexusRegistered;

    // ── Download ──────────────────────────────────────────────────────────

    /// <summary>Called by parent ViewModel to start the actual download.</summary>
    public async Task ExecuteDownloadAsync()
    {
        if (!CanAutoDownload || !IsActionEnabled) return;

        Status     = UpdateStatus.Downloading;
        StatusText = "Downloading...";

        try
        {
            string downloadUrl;

            if (ActiveSource == UpdateSource.MODIO)
            {
                downloadUrl = await GetModioDownloadUrlAsync();
            }
            else
            {
                downloadUrl = await GetNexusDownloadUrlAsync();
            }

            if (string.IsNullOrEmpty(downloadUrl))
                throw new InvalidOperationException("Could not obtain download URL.");

            var progress = new Progress<DownloadProgress>(p =>
            {
                StatusText = p.Text;
                if (p.Percent >= 95)
                    Status = UpdateStatus.Installing;
            });

            await Downloader.DownloadAndInstallAsync(
                downloadUrl,
                _entry.PakFilePath ?? "",
                _modsFolder,
                _backupEnabled,
                progress);

            Status     = UpdateStatus.Done;
            StatusText = "Done";
            Logger.Info($"Download complete: {ModName}");
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

            Application.Current.Dispatcher.InvokeAsync(() =>
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

        var file = await _modioApi.GetLatestFileAsync(_entry.PublishHandle);
        if (file == null)
            throw new InvalidOperationException($"No file found for mod {_entry.PublishHandle}.");

        Logger.Info($"mod.io download URL obtained: {file.FileName}");
        return file.BinaryUrl;
    }

    private async Task<string> GetNexusDownloadUrlAsync()
    {
        if (_nexusApi == null || _entry.NexusModId == null)
            throw new InvalidOperationException("Nexus API or mod ID not available.");

        // Get latest file to obtain fileId
        var latest = await _nexusApi.GetLatestFileAsync(_entry.NexusModId.Value);
        if (latest == null)
            throw new InvalidOperationException($"No files found for Nexus mod {_entry.NexusModId}.");

        var url = await _nexusApi.GetDownloadUrlAsync(_entry.NexusModId.Value, latest.FileId);
        if (string.IsNullOrEmpty(url))
            throw new InvalidOperationException($"No download URL for Nexus mod {_entry.NexusModId}.");

        return url;
    }

    // ── Action dispatch ───────────────────────────────────────────────────

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
         Status != UpdateStatus.Installing);

    private void SwitchSource(string? source)
    {
        if (source == "modio" && HasModio)
            ActiveSource = UpdateSource.MODIO;
        else if (source == "nexus" && HasNexus)
            ActiveSource = UpdateSource.NEXUSMODS;

        OnPropertyChanged(nameof(CanSwitchToModio));
        OnPropertyChanged(nameof(CanSwitchToNexus));
    }

    private void OpenPage()
    {
        var url = ActivePageUrl;
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Warn("OpenPage: " + ex.Message); }
    }

    private void RegisterNexus()
    {
        var input = _nexusUrlInput.Trim();
        var match = System.Text.RegularExpressions.Regex.Match(input, @"mods/(\d+)");
        if (!match.Success)
        {
            MessageBox.Show(
                "Please enter a valid Nexus mod URL.\nExample: https://www.nexusmods.com/baldursgate3/mods/12345",
                "Invalid URL", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var modId = int.Parse(match.Groups[1].Value);
        _entry.NexusModId = modId;
        _entry.NexusUrl   = $"https://www.nexusmods.com/baldursgate3/mods/{modId}";
        _entry.AvailableSources.Add(UpdateSource.NEXUSMODS);
        ActiveSource = UpdateSource.NEXUSMODS;

        _isRegisterExpanded = false;
        OnPropertyChanged(nameof(ShowNexusRegisterPanel));
        OnPropertyChanged(nameof(IsNexusUnregistered));
        OnPropertyChanged(nameof(SourceBadge));
        OnPropertyChanged(nameof(ActionLabel));

        NexusRegistered?.Invoke(this, modId);
    }
}
