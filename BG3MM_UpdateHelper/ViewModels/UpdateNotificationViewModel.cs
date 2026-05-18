using System.Collections.ObjectModel;
using System.Windows;
using BG3MM_UpdateHelper.Models;
using BG3MM_UpdateHelper.Services;

namespace BG3MM_UpdateHelper.ViewModels;

public class UpdateNotificationViewModel : ViewModelBase
{
    private readonly bool      _nexusIsPremium;
    private readonly string    _modsFolder;
    private readonly bool      _backupEnabled;
    private readonly ModioApi? _modioApi;
    private readonly NexusApi? _nexusApi;
    private readonly ModFileIdStore? _fileIdStore;
    private readonly DownloadHistoryStore? _historyStore;
    private readonly Func<Task<List<ModUpdateEntry>>>? _reloadFunc;

    public UpdateNotificationViewModel(
        List<ModUpdateEntry> updates,
        bool nexusIsPremium,
        string modsFolder,
        bool backupEnabled                = false,
        ModioApi? modioApi                = null,
        NexusApi? nexusApi                = null,
        ModFileIdStore? fileIdStore       = null,
        DownloadHistoryStore? historyStore = null,
        Func<Task<List<ModUpdateEntry>>>? reloadFunc = null)
    {
        _nexusIsPremium = nexusIsPremium;
        _modsFolder     = modsFolder;
        _backupEnabled  = backupEnabled;
        _modioApi       = modioApi;
        _nexusApi       = nexusApi;
        _fileIdStore    = fileIdStore;
        _historyStore   = historyStore;
        _reloadFunc     = reloadFunc;

        Entries = new ObservableCollection<UpdateEntryViewModel>(
            updates.Select(u =>
            {
                var vm = new UpdateEntryViewModel(u, nexusIsPremium, modsFolder, backupEnabled, modioApi, nexusApi, fileIdStore, historyStore);
                vm.DownloadRequested += OnDownloadRequested;
                vm.NexusRegistered   += OnNexusRegistered;
                return vm;
            }));

        SelectAllCommand        = new RelayCommand(() => SetAllSelected(true));
        DeselectAllCommand      = new RelayCommand(() => SetAllSelected(false));
        DownloadSelectedCommand = new RelayCommand(
            ExecuteDownloadSelected,
            () => !IsBusy && Entries.Any(e => e.IsSelected && e.CanAutoDownload && e.IsActionEnabled));
        CloseCommand   = new RelayCommand(() => CloseRequested?.Invoke());
        RefreshCommand = new RelayCommand(ExecuteRefresh, () => !IsBusy);

        UpdateSummary();
    }

    // ── Properties ────────────────────────────────────────────────────────

    public ObservableCollection<UpdateEntryViewModel> Entries { get; }

    private string _titleText = "";
    public string TitleText
    {
        get => _titleText;
        private set => SetField(ref _titleText, value);
    }

    private string _summaryText = "";
    public string SummaryText
    {
        get => _summaryText;
        private set => SetField(ref _summaryText, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            SetField(ref _isBusy, value);
            DownloadSelectedCommand.RaiseCanExecuteChanged();
        }
    }

    private string _busyText = "";
    public string BusyText
    {
        get => _busyText;
        private set => SetField(ref _busyText, value);
    }

    // ── Commands ──────────────────────────────────────────────────────────

    public RelayCommand SelectAllCommand        { get; }
    public RelayCommand DeselectAllCommand      { get; }
    public RelayCommand DownloadSelectedCommand { get; }
    public RelayCommand CloseCommand            { get; }
    public RelayCommand RefreshCommand          { get; }

    public event Action?                       CloseRequested;
    public event Action<string, int, int>?     NexusMappingAdded;

    // ── Refresh ──────────────────────────────────────────────────────────────

    private async void ExecuteRefresh()
    {
        if (_reloadFunc == null || IsBusy) return;

        IsBusy   = true;
        BusyText = "Refreshing...";

        try
        {
            var newUpdates = await _reloadFunc();
            var newDict    = newUpdates.ToDictionary(u => u.UUID);

            // Remove entries no longer in the new update list
            var toRemove = Entries
                .Where(e => !newDict.ContainsKey(e.UUID))
                .ToList();
            foreach (var e in toRemove) Entries.Remove(e);

            // Add new entries
            var existing = Entries.Select(e => e.UUID).ToHashSet();
            foreach (var u in newUpdates.Where(u => !existing.Contains(u.UUID)))
            {
                var vm = new UpdateEntryViewModel(u, _nexusIsPremium, _modsFolder,
                    _backupEnabled, _modioApi, _nexusApi, _fileIdStore, _historyStore);
                vm.DownloadRequested += OnDownloadRequested;
                vm.NexusRegistered   += OnNexusRegistered;
                Entries.Add(vm);
            }
        }
        catch (Exception ex) { Logger.Error($"Refresh failed: {ex.Message}"); }
        finally
        {
            IsBusy   = false;
            BusyText = "";
            UpdateSummary();
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    // ── Logic ─────────────────────────────────────────────────────────────

    private void SetAllSelected(bool selected)
    {
        foreach (var e in Entries)
            if (e.CanAutoDownload)
                e.IsSelected = selected;
    }

    private async void ExecuteDownloadSelected()
    {
        var selected = Entries
            .Where(e => e.IsSelected && e.CanAutoDownload && e.IsActionEnabled)
            .ToList();

        if (selected.Count == 0) return;

        IsBusy = true;
        int done = 0;

        foreach (var entry in selected)
        {
            BusyText = $"({++done}/{selected.Count}) {entry.ModName}";
            await entry.ExecuteDownloadAsync();
        }

        IsBusy   = false;
        BusyText = "";
        UpdateSummary();
        DownloadSelectedCommand.RaiseCanExecuteChanged();

        // Summary popup
        var succeeded = selected.Count(e => e.Status == UpdateStatus.Updated);
        var failed    = selected.Count(e => e.Status == UpdateStatus.Failed);
        var msg       = $"{succeeded}/{selected.Count} mods updated successfully.";
        if (failed > 0) msg += $"\n{failed} failed — check Recent Activity for details.";
        MessageBox.Show(msg, "Download Complete", MessageBoxButton.OK,
            failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private async void OnDownloadRequested(UpdateEntryViewModel entry)
    {
        // Single-item download via row button
        await entry.ExecuteDownloadAsync();
        UpdateSummary();
        DownloadSelectedCommand.RaiseCanExecuteChanged();
    }

    private void OnNexusRegistered(UpdateEntryViewModel entry, int modId)
    {
        NexusMappingAdded?.Invoke(entry.UUID, modId, 0);
        UpdateSummary();
        DownloadSelectedCommand.RaiseCanExecuteChanged();
        Logger.Info($"Nexus linked: UUID={entry.UUID}, modId={modId}");
    }

    private void UpdateSummary()
    {
        var total       = Entries.Count;
        var autoCount   = Entries.Count(e => e.CanAutoDownload && e.IsActionEnabled);
        var doneCount   = Entries.Count(e => e.Status == UpdateStatus.Updated);
        var manualCount = Entries.Count(e => !e.CanAutoDownload && !e.IsNexusUnregistered);
        var unregCount  = Entries.Count(e => e.IsNexusUnregistered);

        TitleText = $"{total} Updates Available";

        var parts = new List<string>();
        if (doneCount   > 0) parts.Add($"{doneCount} done");
        if (autoCount   > 0) parts.Add($"{autoCount} auto-download");
        if (manualCount > 0) parts.Add($"{manualCount} manual");
        if (unregCount  > 0) parts.Add($"{unregCount} Nexus unlinked");
        SummaryText = string.Join(" · ", parts);
    }
}
