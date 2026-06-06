using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using BG3ModHelper.Models;
using BG3ModHelper.Services;

namespace BG3ModHelper.ViewModels;

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

        var allVms = updates.Select(u =>
        {
            var vm = new UpdateEntryViewModel(u, nexusIsPremium, modsFolder, backupEnabled, modioApi, nexusApi, fileIdStore, historyStore);
            vm.DownloadRequested += OnDownloadRequested;
            vm.PageOpenRequested += OnPageOpenRequested;
            vm.NexusRegistered   += OnNexusRegistered;
            vm.CancelRequested   += OnCancelRequested;
            return vm;
        }).ToList();

        Entries = new ObservableCollection<UpdateEntryViewModel>(GroupEntries(allVms));

        // Split into active / inactive sections
        ActiveEntries   = new ObservableCollection<UpdateEntryViewModel>(Entries.Where(e => e.IsActive));
        InactiveEntries = new ObservableCollection<UpdateEntryViewModel>(Entries.Where(e => !e.IsActive));

        // Inactive entries are deselected by default — user opts in explicitly
        foreach (var e in InactiveEntries)
            e.IsSelected = false;

        // Subscribe so master-checkbox states stay in sync as rows are toggled
        foreach (var vm in ActiveEntries)
            vm.PropertyChanged += OnEntrySelectionChanged;
        foreach (var vm in InactiveEntries)
            vm.PropertyChanged += OnEntrySelectionChanged;

        ActiveEntriesView   = CollectionViewSource.GetDefaultView(ActiveEntries);
        InactiveEntriesView = CollectionViewSource.GetDefaultView(InactiveEntries);

        // Keep EntriesView for backward compat (used by download/skip logic)
        EntriesView = CollectionViewSource.GetDefaultView(Entries);

        foreach (var view in new[] { ActiveEntriesView, InactiveEntriesView, EntriesView })
        {
            if (view is System.ComponentModel.ICollectionViewLiveShaping lv)
            {
                lv.IsLiveSorting = true;
                lv.LiveSortingProperties.Add(nameof(UpdateEntryViewModel.SortPreserveOrder));
                lv.LiveSortingProperties.Add(nameof(UpdateEntryViewModel.SortName));
                lv.LiveSortingProperties.Add(nameof(UpdateEntryViewModel.SortSourceOrder));
            }
        }

        SortActiveByNameCommand   = new RelayCommand(() => ToggleSortActive(SortCol.Name));
        SortActiveBySourceCommand = new RelayCommand(() => ToggleSortActive(SortCol.Source));
        SortActiveByLockCommand   = new RelayCommand(() => ToggleSortActive(SortCol.Lock));
        SortInactiveByNameCommand   = new RelayCommand(() => ToggleSortInactive(SortCol.Name));
        SortInactiveBySourceCommand = new RelayCommand(() => ToggleSortInactive(SortCol.Source));
        SortInactiveByLockCommand   = new RelayCommand(() => ToggleSortInactive(SortCol.Lock));
        ApplySort();

        SelectAllActiveCommand   = new RelayCommand(() => SetSectionSelected(active: true,  selected: true));
        DeselectAllActiveCommand = new RelayCommand(() => SetSectionSelected(active: true,  selected: false));
        SelectAllInactiveCommand   = new RelayCommand(() => SetSectionSelected(active: false, selected: true));
        DeselectAllInactiveCommand = new RelayCommand(() => SetSectionSelected(active: false, selected: false));
        SelectAllCommand        = new RelayCommand(() => SetAllSelected(true));
        DeselectAllCommand      = new RelayCommand(() => SetAllSelected(false));
        DownloadSelectedCommand = new RelayCommand(
            ExecuteDownloadSelected,
            () => !IsBusy && !IsWebViewPanelOpen &&
                  Entries.Any(e => e.IsSelected && e.IsActionEnabled &&
                                   (e.CanAutoDownload ||
                                    (!e.CanAutoDownload && e.HasNexus && !e.IsNexusUnregistered))));
        CloseCommand        = new RelayCommand(() => CloseRequested?.Invoke());
        RefreshCommand      = new RelayCommand(ExecuteRefresh, () => !IsBusy);
        CloseWebViewCommand = new RelayCommand(CloseWebViewPanel);
        SkipCommand         = new RelayCommand(ExecuteSkip);

        // subscribe for the lifetime of the window
        UnifiedDownloadQueue.Instance.OnNxmCompleted += OnNxmCompleted;
        UnifiedDownloadQueue.Instance.OnNxmProgress  += OnNxmProgress;

        UpdateSummary();
    }

    // === Properties ===

    public ObservableCollection<UpdateEntryViewModel> Entries         { get; }
    public ObservableCollection<UpdateEntryViewModel> ActiveEntries   { get; }
    public ObservableCollection<UpdateEntryViewModel> InactiveEntries { get; }

    public string ActiveSectionHeader   => $"Active Mods ({ActiveEntries.Count})";
    public string InactiveSectionHeader => $"Inactive Mods ({InactiveEntries.Count})";

    // Suppresses per-entry PropertyChanged notifications while a mass select/deselect loop is running.
    // Prevents intermediate null states from corrupting the IsThreeState click cycle via binding feedback.
    private bool _suppressMasterNotify;

    /// <summary>
    /// Tri-state master checkbox for the Active section.
    /// null = mixed, true = all selected, false = none selected.
    /// </summary>
    public bool? ActiveMasterChecked
    {
        get
        {
            var queued = ActiveEntries.Where(e => e.CanBeQueued).ToList();
            if (queued.Count == 0) return false;
            var selected = queued.Count(e => e.IsSelected);
            return selected == 0 ? false : selected == queued.Count ? true : (bool?)null;
        }
        set
        {
            // IsThreeState cycle: false→true→null→false.
            // Clicking from true passes null (not false), so treat null as deselect.
            SetSectionSelected(active: true, selected: value == true);
        }
    }

    /// <summary>
    /// Tri-state master checkbox for the Inactive section.
    /// null = mixed, true = all selected, false = none selected.
    /// </summary>
    public bool? InactiveMasterChecked
    {
        get
        {
            var queued = InactiveEntries.Where(e => e.CanBeQueued).ToList();
            if (queued.Count == 0) return false;
            var selected = queued.Count(e => e.IsSelected);
            return selected == 0 ? false : selected == queued.Count ? true : (bool?)null;
        }
        set
        {
            // IsThreeState cycle: false→true→null→false.
            // Clicking from true passes null (not false), so treat null as deselect.
            SetSectionSelected(active: false, selected: value == true);
        }
    }

    private void OnEntrySelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UpdateEntryViewModel.IsSelected)) return;
        if (_suppressMasterNotify) return;
        if (sender is UpdateEntryViewModel vm)
        {
            if (ActiveEntries.Contains(vm))
                OnPropertyChanged(nameof(ActiveMasterChecked));
            else
                OnPropertyChanged(nameof(InactiveMasterChecked));
        }
    }

    public ICollectionView ActiveEntriesView   { get; }
    public ICollectionView InactiveEntriesView { get; }

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

    private bool _isWebViewPanelOpen;
    public bool IsWebViewPanelOpen
    {
        get => _isWebViewPanelOpen;
        private set
        {
            SetField(ref _isWebViewPanelOpen, value);
            DownloadSelectedCommand.RaiseCanExecuteChanged();
        }
    }

    private string _webViewCurrentUrl = "";
    public string WebViewCurrentUrl
    {
        get => _webViewCurrentUrl;
        private set => SetField(ref _webViewCurrentUrl, value);
    }

    private string _webViewStatusText = "";
    public string WebViewStatusText
    {
        get => _webViewStatusText;
        private set => SetField(ref _webViewStatusText, value);
    }

    private bool _isSkipVisible;
    public bool IsSkipVisible
    {
        get => _isSkipVisible;
        private set => SetField(ref _isSkipVisible, value);
    }

    // === Commands ===

    public RelayCommand SelectAllActiveCommand     { get; }
    public RelayCommand DeselectAllActiveCommand   { get; }
    public RelayCommand SelectAllInactiveCommand   { get; }
    public RelayCommand DeselectAllInactiveCommand { get; }
    public RelayCommand SelectAllCommand           { get; }
    public RelayCommand DeselectAllCommand         { get; }
    public RelayCommand DownloadSelectedCommand    { get; }
    public RelayCommand CloseCommand               { get; }
    public RelayCommand RefreshCommand             { get; }
    public RelayCommand CloseWebViewCommand        { get; }
    public RelayCommand SkipCommand                { get; }

    public event Action?                       CloseRequested;
    public event Action?                       LoginRequired;
    public event Action<string, int, int>?     NexusMappingAdded;

    // === Sort ===

    private enum SortCol { Name, Source, Lock }

    // Active section sort state (independent from Inactive)
    private SortCol _activeSortColumn    = SortCol.Name;
    private bool    _activeSortAscending = true;

    // Inactive section sort state (independent from Active)
    private SortCol _inactiveSortColumn    = SortCol.Name;
    private bool    _inactiveSortAscending = true;

    public ICollectionView EntriesView { get; }

    // ── Active header texts ──────────────────────────────────────────────────
    public string ActiveNameSortHeader   => _activeSortColumn == SortCol.Name   ? (_activeSortAscending ? "Name ↑"   : "Name ↓")   : "Name ⇅";
    public string ActiveSourceSortHeader => _activeSortColumn == SortCol.Source ? (_activeSortAscending ? "Source ↑" : "Source ↓") : "Source ⇅";
    public string ActiveLockSortHeader   => _activeSortColumn == SortCol.Lock   ? (_activeSortAscending ? "↑" : "↓") : "⇅";

    // ── Inactive header texts ────────────────────────────────────────────────
    public string InactiveNameSortHeader   => _inactiveSortColumn == SortCol.Name   ? (_inactiveSortAscending ? "Name ↑"   : "Name ↓")   : "Name ⇅";
    public string InactiveSourceSortHeader => _inactiveSortColumn == SortCol.Source ? (_inactiveSortAscending ? "Source ↑" : "Source ↓") : "Source ⇅";
    public string InactiveLockSortHeader   => _inactiveSortColumn == SortCol.Lock   ? (_inactiveSortAscending ? "↑" : "↓") : "⇅";

    // Keep old names as pass-through so any lingering bindings don't crash
    public string NameSortHeader   => ActiveNameSortHeader;
    public string SourceSortHeader => ActiveSourceSortHeader;

    // ── Active commands ──────────────────────────────────────────────────────
    public RelayCommand SortActiveByNameCommand   { get; }
    public RelayCommand SortActiveBySourceCommand { get; }
    public RelayCommand SortActiveByLockCommand   { get; }

    // ── Inactive commands ────────────────────────────────────────────────────
    public RelayCommand SortInactiveByNameCommand   { get; }
    public RelayCommand SortInactiveBySourceCommand { get; }
    public RelayCommand SortInactiveByLockCommand   { get; }

    // Kept for backward compat
    public RelayCommand SortByNameCommand   => SortActiveByNameCommand;
    public RelayCommand SortBySourceCommand => SortActiveBySourceCommand;

    private void ToggleSortActive(SortCol col)
    {
        if (_activeSortColumn == col) _activeSortAscending = !_activeSortAscending;
        else { _activeSortColumn = col; _activeSortAscending = true; }
        ApplySortToView(ActiveEntriesView, _activeSortColumn, _activeSortAscending);
        OnPropertyChanged(nameof(ActiveNameSortHeader));
        OnPropertyChanged(nameof(ActiveSourceSortHeader));
        OnPropertyChanged(nameof(ActiveLockSortHeader));
    }

    private void ToggleSortInactive(SortCol col)
    {
        if (_inactiveSortColumn == col) _inactiveSortAscending = !_inactiveSortAscending;
        else { _inactiveSortColumn = col; _inactiveSortAscending = true; }
        ApplySortToView(InactiveEntriesView, _inactiveSortColumn, _inactiveSortAscending);
        OnPropertyChanged(nameof(InactiveNameSortHeader));
        OnPropertyChanged(nameof(InactiveSourceSortHeader));
        OnPropertyChanged(nameof(InactiveLockSortHeader));
    }

    private static void ApplySortToView(ICollectionView view, SortCol col, bool ascending)
    {
        var dir = ascending ? ListSortDirection.Ascending : ListSortDirection.Descending;
        view.SortDescriptions.Clear();
        switch (col)
        {
            case SortCol.Name:
                view.SortDescriptions.Add(new SortDescription("SortName", dir));
                break;
            case SortCol.Source:
                view.SortDescriptions.Add(new SortDescription("SortSourceOrder", dir));
                view.SortDescriptions.Add(new SortDescription("SortName", ListSortDirection.Ascending));
                break;
            case SortCol.Lock:
                view.SortDescriptions.Add(new SortDescription("SortPreserveOrder", dir));
                view.SortDescriptions.Add(new SortDescription("SortName", ListSortDirection.Ascending));
                break;
        }
    }

    private void ApplySort()
    {
        // Apply initial sort to both sections independently (called once at construction)
        ApplySortToView(ActiveEntriesView,   _activeSortColumn,   _activeSortAscending);
        ApplySortToView(InactiveEntriesView, _inactiveSortColumn, _inactiveSortAscending);
        // EntriesView mirrors Active sort for legacy download/skip logic
        ApplySortToView(EntriesView,         _activeSortColumn,   _activeSortAscending);
    }

    private void SetSectionSelected(bool active, bool selected)
    {
        // Suppress per-entry notifications during the loop to prevent intermediate null states
        // from corrupting the IsThreeState click cycle via binding feedback.
        _suppressMasterNotify = true;
        try
        {
            var source = active ? ActiveEntries : InactiveEntries;
            foreach (var e in source)
                e.IsSelected = selected;
        }
        finally
        {
            _suppressMasterNotify = false;
        }

        // Fire exactly once after all entries are updated
        OnPropertyChanged(active ? nameof(ActiveMasterChecked) : nameof(InactiveMasterChecked));
    }

    // === WebView slide state ===

    private Queue<UpdateEntryViewModel>        _nexusFreeQueue  = new();
    private UpdateEntryViewModel?              _currentSlideEntry;
    private int                                _webViewTotal;
    private int                                _webViewProgress;
    // nxm mod ID → entry: tracks which entry corresponds to each nxm download
    private readonly Dictionary<int, UpdateEntryViewModel> _downloadingByNxmId = new();

    // === Cancel tracking ===
    // Items currently enqueued in the active batch (set during ExecuteDownloadSelected)
    private List<UpdateEntryViewModel> _activeAutoItems = [];

    // === Refresh ===

    /// <summary>
    /// Lightweight local refresh — no API calls.
    /// Removes entries that are already up-to-date based on local data only:
    ///   1. Status == Updated  (downloaded this session)
    ///   2. Nexus fileId now matches the latest DB fileId (downloaded in a previous session)
    /// To discover new updates, close this window and run Check Updates again.
    /// </summary>
    private void ExecuteRefresh()
    {
        if (IsBusy) return;

        var toRemove = Entries
            .Where(e => e.Status == UpdateStatus.Updated ||
                        (e.NexusFileId > 0 && _fileIdStore != null &&
                         _fileIdStore.GetFileId(e.UUID) == e.NexusFileId))
            .ToList();

        foreach (var e in toRemove)
        {
            e.PropertyChanged -= OnEntrySelectionChanged;
            Entries.Remove(e);
            ActiveEntries.Remove(e);
            InactiveEntries.Remove(e);
        }

        UpdateSummary();
        RefreshCommand.RaiseCanExecuteChanged();
    }

    // === Logic ===

    private void SetAllSelected(bool selected)
    {
        foreach (var e in Entries)
            if (e.CanBeQueued)
                e.IsSelected = selected;
    }

    private async void ExecuteDownloadSelected()
    {
        try
        {
            var autoItems = Entries
                .Where(e => e.IsSelected && e.CanAutoDownload && e.IsActionEnabled)
                .ToList();

            var freeItems = Entries
                .Where(e => e.IsSelected && e.IsActionEnabled &&
                            !e.CanAutoDownload && e.HasNexus && !e.IsNexusUnregistered)
                .ToList();

            if (autoItems.Count == 0 && freeItems.Count == 0) return;

            // Auto-download items (mod.io + Nexus Premium) — routed through UnifiedDownloadQueue
            if (autoItems.Count > 0)
            {
                IsBusy          = true;
                _activeAutoItems = autoItems;
                var total       = autoItems.Count;
                var done        = 0;
                var completions = autoItems.Select(_ => new TaskCompletionSource()).ToArray();

                for (int i = 0; i < autoItems.Count; i++)
                {
                    var entry = autoItems[i];
                    var tcs   = completions[i];
                    Services.UnifiedDownloadQueue.Instance.Enqueue(new Services.DownloadJob
                    {
                        Kind    = Services.DownloadJobKind.AutoUpdate,
                        Label   = entry.ModName,
                        Execute = async () =>
                        {
                            var n = System.Threading.Interlocked.Increment(ref done);
                            _ = Application.Current.Dispatcher.InvokeAsync(
                                () => BusyText = $"({n}/{total}) {entry.ModName}");
                            try
                            {
                                await entry.ExecuteDownloadAsync(
                                    onProgress: p => Services.UnifiedDownloadQueue.Instance.ReportProgress(p));
                            }
                            finally { tcs.TrySetResult(); }
                        }
                    });
                }

                await Task.WhenAll(completions.Select(t => t.Task));
                _activeAutoItems = [];
                IsBusy   = false;
                BusyText = "";
                UpdateSummary();
                DownloadSelectedCommand.RaiseCanExecuteChanged();

                // Auto-download items that hit 403 (manager downloads disabled) → merge into manual queue
                var manualItems = autoItems
                    .Where(e => e.Status == UpdateStatus.ManualRequired)
                    .ToList();
                if (manualItems.Count > 0)
                {
                    Logger.Info($"ExecuteDownloadSelected: {manualItems.Count} mod(s) require manual download — routing to WebView queue");
                    freeItems = freeItems.Concat(manualItems).ToList();
                }
            }

            // Nexus Free slide — open WebView panel
            if (freeItems.Count > 0)
            {
                _nexusFreeQueue  = new Queue<UpdateEntryViewModel>(freeItems);
                _webViewTotal    = freeItems.Count;
                _webViewProgress = 0;
                Services.UnifiedDownloadQueue.Instance.OnNxmQueued += OnNxmQueued;
                AdvanceSlide();
            }
            else if (autoItems.Count > 0)
            {
                var succeeded = autoItems.Count(e => e.Status == UpdateStatus.Updated);
                var failed    = autoItems.Count(e => e.Status == UpdateStatus.Failed);
                var msg       = $"{succeeded}/{autoItems.Count} mods updated successfully.";
                if (failed > 0) msg += $"\n{failed} failed — check Recent Activity for details.";
                MessageBox.Show(msg, "Download Complete", MessageBoxButton.OK,
                    failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            IsBusy   = false;
            BusyText = "";
            Logger.Error($"ExecuteDownloadSelected failed: {ex.Message}");
        }
    }

    // === WebView slide logic ===

    private void AdvanceSlide()
    {
        if (_nexusFreeQueue.Count == 0)
        {
            CloseWebViewPanel();
            return;
        }

        _currentSlideEntry = _nexusFreeQueue.Dequeue();
        _webViewProgress++;

        var url = _currentSlideEntry.ActivePageUrl ?? "";
        if (!string.IsNullOrEmpty(url))
            url = url.Contains('?') ? $"{url}&tab=files" : $"{url}?tab=files";

        WebViewCurrentUrl  = url;
        WebViewStatusText  = $"{_webViewProgress} / {_webViewTotal}  —  {_currentSlideEntry.ModName}";
        IsSkipVisible      = _nexusFreeQueue.Count > 0;
        IsWebViewPanelOpen = true;
    }

    private void ExecuteSkip() => AdvanceSlide();

    private void OnNxmQueued(NxmUrl url, int _)
    {
        if (_currentSlideEntry == null) return;
        // treat as current entry if modId matches or nxm arrived while this entry's page was open
        if (_currentSlideEntry.NexusModId.HasValue &&
            _currentSlideEntry.NexusModId.Value != url.NexusModId) return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            _downloadingByNxmId[url.NexusModId] = _currentSlideEntry;
            _currentSlideEntry.Status = UpdateStatus.Downloading;
            AdvanceSlide();
        });
    }

    private void OnNxmCompleted(NxmQueueItem item, bool success, string? errorReason)
    {
        // first: tracked dict populated by OnNxmQueued
        UpdateEntryViewModel? entry = null;
        if (_downloadingByNxmId.TryGetValue(item.Url.NexusModId, out var tracked))
        {
            entry = tracked;
            _downloadingByNxmId.Remove(item.Url.NexusModId);
        }
        else
        {
            // fallback: search by NexusModId
            entry = Entries.FirstOrDefault(e =>
                e.NexusModId.HasValue && e.NexusModId.Value == item.Url.NexusModId);
        }
        if (entry == null) return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            if (entry.Status == UpdateStatus.Downloading || entry.Status == UpdateStatus.Applying)
            {
                entry.Status     = success ? UpdateStatus.Updated : UpdateStatus.Failed;
                entry.StatusText = success ? "Updated" : "Failed";
            }
            UpdateSummary();
            DownloadSelectedCommand.RaiseCanExecuteChanged();

            if (!success && errorReason != null)
            {
                var msg = errorReason.Contains("isn't correct for this user")
                    ? $"Nexus download failed — {entry.ModName}\n\nAPI key does not match the WebView login account.\nEnter an API key for the currently logged-in account in Settings."
                    : $"Nexus download failed — {entry.ModName}\n\n{errorReason}";
                MessageBox.Show(msg, "Download Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });
    }

    private void CloseWebViewPanel()
    {
        UnifiedDownloadQueue.Instance.OnNxmQueued -= OnNxmQueued;
        _currentSlideEntry = null;
        _nexusFreeQueue.Clear();
        _downloadingByNxmId.Clear();
        IsWebViewPanelOpen = false;
        IsSkipVisible      = false;
        WebViewCurrentUrl  = "";
        WebViewStatusText  = "";
        UpdateSummary();
        DownloadSelectedCommand.RaiseCanExecuteChanged();
    }

    public void OnWindowClosing()
    {
        UnifiedDownloadQueue.Instance.OnNxmQueued    -= OnNxmQueued;
        UnifiedDownloadQueue.Instance.OnNxmCompleted -= OnNxmCompleted;
        UnifiedDownloadQueue.Instance.OnNxmProgress  -= OnNxmProgress;
    }

    private void OnCancelRequested(UpdateEntryViewModel entry)
    {
        // 1. Immediately cancel the active download
        entry.CancelDownloadNow();

        // 2. Find batch items that haven't started yet
        var pendingItems = _activeAutoItems
            .Where(e => e != entry && e.Status == UpdateStatus.Pending)
            .ToList();

        if (pendingItems.Count == 0) return;

        // 3. Ask what to do with the remaining queue
        //    Yes = 전체 취소 / No = 다음 항목 계속
        var result = MessageBox.Show(
            $"현재 다운로드가 취소되었습니다.\n\n대기 중인 {pendingItems.Count}개 항목도 전체 취소할까요?\n\n예(Yes) — 전체 취소\n아니오(No) — 다음 항목 계속",
            "다운로드 취소",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (result == MessageBoxResult.Yes)
        {
            foreach (var item in pendingItems)
            {
                item.Status     = UpdateStatus.Skipped;
                item.StatusText = "Cancelled";
            }
        }
    }

    private async void OnDownloadRequested(UpdateEntryViewModel entry)
    {
        try
        {
            // Route through UnifiedDownloadQueue so IsScanning, activity log, and
            // progress bar all work the same as the "Download Selected" batch path.
            var tcs = new TaskCompletionSource();
            Services.UnifiedDownloadQueue.Instance.Enqueue(new Services.DownloadJob
            {
                Kind    = Services.DownloadJobKind.AutoUpdate,
                Label   = entry.ModName,
                Execute = async () =>
                {
                    try
                    {
                        await entry.ExecuteDownloadAsync(
                            onProgress: p => Services.UnifiedDownloadQueue.Instance.ReportProgress(p));
                    }
                    finally { tcs.TrySetResult(); }
                }
            });
            await tcs.Task;
            UpdateSummary();
            DownloadSelectedCommand.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            Logger.Error($"OnDownloadRequested failed: {ex.Message}");
        }
    }

    private void OnPageOpenRequested(UpdateEntryViewModel entry)
    {
        if (!WebViewHelper.IsLoggedIn())
        {
            LoginRequired?.Invoke();
            return;
        }

        if (IsBusy) return;

        var url = entry.ActivePageUrl ?? "";
        if (!string.IsNullOrEmpty(url))
            url = url.Contains('?') ? $"{url}&tab=files" : $"{url}?tab=files";

        if (IsWebViewPanelOpen)
        {
            // Panel already open — just navigate to the new mod's page
            _currentSlideEntry = entry;
            WebViewStatusText  = entry.ModName;
            WebViewCurrentUrl  = url;
            return;
        }

        _currentSlideEntry = entry;
        _nexusFreeQueue.Clear();
        _webViewTotal    = 1;
        _webViewProgress = 1;
        UnifiedDownloadQueue.Instance.OnNxmQueued += OnNxmQueued;

        WebViewCurrentUrl  = url;
        WebViewStatusText  = entry.ModName;
        IsWebViewPanelOpen = true;
    }

    private void OnNxmProgress(NxmQueueItem item, DownloadProgress progress)
    {
        UpdateEntryViewModel? entry = null;
        if (_downloadingByNxmId.TryGetValue(item.Url.NexusModId, out var tracked))
            entry = tracked;
        else
            entry = Entries.FirstOrDefault(e =>
                e.NexusModId.HasValue && e.NexusModId.Value == item.Url.NexusModId);
        if (entry == null) return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            entry.StatusText = progress.Text;
            if ((progress.Text.StartsWith("Extracting") || progress.Text.StartsWith("Applying") || progress.Text.StartsWith("Backing"))
                && entry.Status == UpdateStatus.Downloading)
                entry.Status = UpdateStatus.Applying;
        });
    }

    private void OnNexusRegistered(UpdateEntryViewModel entry, int modId)
    {
        NexusMappingAdded?.Invoke(entry.UUID, modId, 0);
        UpdateSummary();
        DownloadSelectedCommand.RaiseCanExecuteChanged();
        Logger.Info($"Nexus linked: UUID={entry.UUID}, modId={modId}");
    }

    /// <summary>
    /// Groups VMs by (NexusModId, NexusFileId). Groups of 2+ get a header + children.
    /// Returns only headers (children are attached to their header, not in the main list).
    /// </summary>
    private static IEnumerable<UpdateEntryViewModel> GroupEntries(IReadOnlyList<UpdateEntryViewModel> vms)
    {
        var children     = new HashSet<UpdateEntryViewModel>();
        var bundleNoise  = new HashSet<UpdateEntryViewModel>();

        var groups = vms
            .Where(vm => vm.NexusModId.HasValue && vm.NexusModId.Value != 0 && vm.NexusFileId != 0)
            .GroupBy(vm => (vm.NexusModId!.Value, vm.NexusFileId))
            .Where(g => g.Count() > 1);

        foreach (var g in groups)
        {
            if (IsCoherentPakGroup(g))
            {
                var header = g.First();
                foreach (var child in g.Skip(1))
                {
                    header.AddGroupChild(child);
                    children.Add(child);
                }
            }
            else
            {
                // Bundle reupload noise: handle mods that have their own mod.io identity.
                foreach (var vm in g.Where(vm => vm.ModioPublishHandle != 0))
                {
                    if (vm.HasModio)
                        // mod.io update also found → keep entry, strip noisy Nexus source
                        vm.StripNexusSource();
                    else
                        // Only Nexus-bundle source, no real update → discard entry entirely
                        bundleNoise.Add(vm);
                }
            }
        }

        return vms.Where(vm => !children.Contains(vm) && !bundleNoise.Contains(vm));
    }

    /// <summary>
    /// Rejects bundle repacks where unrelated mods are zipped together.
    /// Legitimate multi-pak mods are consistent: either ALL paks have a mod.io handle
    /// or NONE do. A mix (some have, some don't) almost always means an unauthorized
    /// bundle reupload containing paks from different sources.
    /// </summary>
    private static bool IsCoherentPakGroup(IEnumerable<UpdateEntryViewModel> group)
    {
        var distinctHandlePresence = group
            .Select(vm => vm.ModioPublishHandle != 0)
            .Distinct()
            .Count();
        return distinctHandlePresence == 1;
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
