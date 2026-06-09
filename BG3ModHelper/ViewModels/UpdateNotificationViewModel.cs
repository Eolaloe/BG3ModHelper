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
    private readonly UserModLinkStore?    _userLinkStore;
    private readonly NexusIdDatabase?    _nexusDb;
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
        Func<Task<List<ModUpdateEntry>>>? reloadFunc = null,
        UserModLinkStore? userLinkStore   = null,
        NexusIdDatabase? nexusDb          = null)
    {
        _nexusIsPremium = nexusIsPremium;
        _modsFolder     = modsFolder;
        _backupEnabled  = backupEnabled;
        _modioApi       = modioApi;
        _nexusApi       = nexusApi;
        _fileIdStore    = fileIdStore;
        _historyStore   = historyStore;
        _userLinkStore  = userLinkStore;
        _nexusDb        = nexusDb;
        _reloadFunc     = reloadFunc;

        var allVms = updates.Select(u =>
        {
            var vm = new UpdateEntryViewModel(u, nexusIsPremium, modsFolder, backupEnabled, modioApi, nexusApi, fileIdStore, historyStore, userLinkStore, nexusDb);
            vm.DownloadRequested       += OnDownloadRequested;
            vm.PageOpenRequested       += OnPageOpenRequested;
            vm.NexusRegistered         += OnNexusRegistered;
            vm.CancelRequested         += OnCancelRequested;
            vm.DisambiguationRequested += OnDisambiguationRequested;
            return vm;
        }).ToList();

        Entries = new ObservableCollection<UpdateEntryViewModel>(GroupEntries(allVms));

        // Split into active / inactive / sync sections
        ActiveEntries   = new ObservableCollection<UpdateEntryViewModel>(Entries.Where(e => !e.IsSyncRequired && e.IsActive));
        InactiveEntries = new ObservableCollection<UpdateEntryViewModel>(Entries.Where(e => !e.IsSyncRequired && !e.IsActive));
        SyncEntries     = new ObservableCollection<UpdateEntryViewModel>(Entries.Where(e => e.IsSyncRequired));

        // Inactive + Sync entries are deselected by default — user opts in explicitly
        foreach (var e in InactiveEntries)
            e.IsSelected = false;
        foreach (var e in SyncEntries)
            e.IsSelected = false;

        // Subscribe so master-checkbox states stay in sync as rows are toggled
        foreach (var vm in ActiveEntries)
            vm.PropertyChanged += OnEntrySelectionChanged;
        foreach (var vm in InactiveEntries)
            vm.PropertyChanged += OnEntrySelectionChanged;
        foreach (var vm in SyncEntries)
            vm.PropertyChanged += OnEntrySelectionChanged;

        ActiveEntriesView   = CollectionViewSource.GetDefaultView(ActiveEntries);
        InactiveEntriesView = CollectionViewSource.GetDefaultView(InactiveEntries);
        SyncEntriesView     = CollectionViewSource.GetDefaultView(SyncEntries);

        // Keep EntriesView for backward compat (used by download/skip logic)
        EntriesView = CollectionViewSource.GetDefaultView(Entries);

        foreach (var view in new[] { ActiveEntriesView, InactiveEntriesView, SyncEntriesView, EntriesView })
        {
            if (view is System.ComponentModel.ICollectionViewLiveShaping lv)
            {
                lv.IsLiveSorting = true;
                lv.LiveSortingProperties.Add(nameof(UpdateEntryViewModel.IsAmbiguous));
                lv.LiveSortingProperties.Add(nameof(UpdateEntryViewModel.SortPreserveOrder));
                lv.LiveSortingProperties.Add(nameof(UpdateEntryViewModel.SortName));
                lv.LiveSortingProperties.Add(nameof(UpdateEntryViewModel.SortSourceOrder));
                lv.LiveSortingProperties.Add(nameof(UpdateEntryViewModel.LocalAuthor));
            }
        }

        SortActiveByNameCommand   = new RelayCommand(() => ToggleSortActive(SortCol.Name));
        SortActiveBySourceCommand = new RelayCommand(() => ToggleSortActive(SortCol.Source));
        SortActiveByLockCommand   = new RelayCommand(() => ToggleSortActive(SortCol.Lock));
        SortActiveByAuthorCommand = new RelayCommand(() => ToggleSortActive(SortCol.Author));
        SortInactiveByNameCommand   = new RelayCommand(() => ToggleSortInactive(SortCol.Name));
        SortInactiveBySourceCommand = new RelayCommand(() => ToggleSortInactive(SortCol.Source));
        SortInactiveByLockCommand   = new RelayCommand(() => ToggleSortInactive(SortCol.Lock));
        SortInactiveByAuthorCommand = new RelayCommand(() => ToggleSortInactive(SortCol.Author));
        SortSyncByNameCommand   = new RelayCommand(() => ToggleSortSync(SortCol.Name));
        SortSyncByAuthorCommand = new RelayCommand(() => ToggleSortSync(SortCol.Author));
        ApplySort();

        SelectAllActiveCommand   = new RelayCommand(() => SetSectionSelected(active: true,  selected: true));
        DeselectAllActiveCommand = new RelayCommand(() => SetSectionSelected(active: true,  selected: false));
        SelectAllInactiveCommand   = new RelayCommand(() => SetSectionSelected(active: false, selected: true));
        DeselectAllInactiveCommand = new RelayCommand(() => SetSectionSelected(active: false, selected: false));
        SelectAllSyncCommand   = new RelayCommand(() => SetSyncSelected(true));
        DeselectAllSyncCommand = new RelayCommand(() => SetSyncSelected(false));
        SelectAllCommand        = new RelayCommand(() => SetAllSelected(true));
        DeselectAllCommand      = new RelayCommand(() => SetAllSelected(false));
        DownloadSelectedCommand = new RelayCommand(
            ExecuteDownloadSelected,
            () => !IsBusy && !IsWebViewPanelOpen &&
                  Entries.Any(e => e.IsSelected && e.IsActionEnabled &&
                                   (e.CanAutoDownload ||
                                    (!e.CanAutoDownload && e.HasNexus && !e.IsNexusUnregistered))));
        StopAllCommand              = new RelayCommand(ExecuteStopAll, () => IsBusy);
        CloseCommand                = new RelayCommand(() => CloseRequested?.Invoke());
        RefreshCommand              = new RelayCommand(ExecuteRefresh, () => !IsBusy);
        CloseWebViewCommand         = new RelayCommand(CloseWebViewPanel);
        SkipCommand                 = new RelayCommand(ExecuteSkip);
        CheckRateLimitsCommand      = new RelayCommand(ExecuteCheckRateLimits, () => _nexusApi != null && !RateLimitChecking);
        ShowIdentifiedListCommand   = new RelayCommand(
            () => ShowIdentifiedListRequested?.Invoke());

        // subscribe for the lifetime of the window
        UnifiedDownloadQueue.Instance.OnNxmCompleted += OnNxmCompleted;
        UnifiedDownloadQueue.Instance.OnNxmProgress  += OnNxmProgress;
        NexusApi.RateLimitsUpdated += OnNexusRateLimitsUpdated;

        UpdateSummary();
    }

    // === Identified mods list ===

    public event Action? ShowIdentifiedListRequested;
    public RelayCommand ShowIdentifiedListCommand { get; }

    public IReadOnlyList<UpdateEntryViewModel> IdentifiedEntries =>
        Entries.Where(e => e.ShowChangeButton).ToList();

    public bool   HasIdentifiedEntries    => Entries.Any(e => e.ShowChangeButton);
    public string IdentifiedCountLabel    => $"Identified  ({Entries.Count(e => e.ShowChangeButton)})";

    /// <summary>
    /// Mods that are in UserModLinkStore (manually identified) but do NOT appear
    /// in the current update-check list (e.g. already up to date or not installed this session).
    /// Shown in the second section of the Identified Mods popup.
    /// </summary>
    public IReadOnlyList<IdentifiedLinkEntryViewModel> LinkedOnlyEntries
    {
        get
        {
            if (_userLinkStore == null) return Array.Empty<IdentifiedLinkEntryViewModel>();

            // Pak file names (with .pak, lowercase) already in the update list
            var inUpdateList = Entries
                .Where(e => !string.IsNullOrEmpty(e.PakFilePath))
                .Select(e => System.IO.Path.GetFileName(e.PakFilePath).ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var result = new List<IdentifiedLinkEntryViewModel>();
            foreach (var (pakName, link) in _userLinkStore.GetAllNexusLinks())
            {
                // pakName is already normalized with .pak (from UserModLinkStore keys)
                if (inUpdateList.Contains(pakName)) continue;

                string? modName = null, author = null;
                if (_nexusDb != null)
                {
                    var dbEntries = _nexusDb.LookupByPakFileName(pakName);
                    var match = dbEntries.FirstOrDefault(e => e.NexusModId == link.ModId)
                                ?? dbEntries.FirstOrDefault();
                    if (match != null) { modName = match.NexusModName; author = match.NexusUploadedBy; }
                }

                result.Add(new IdentifiedLinkEntryViewModel(
                    pakName, link.ModId, modName, author, _userLinkStore, _nexusDb,
                    uuid: link.Uuid));
            }
            return result;
        }
    }

    // === Nexus API rate limit display ===

    private int _nexusHourlyRemaining = NexusApi.LastKnownHourlyRemaining;
    private int _nexusDailyRemaining  = NexusApi.LastKnownDailyRemaining;

    public bool   HasNexusRateLimitData => _nexusHourlyRemaining >= 0;
    public string NexusRateLimitText    =>
        _nexusHourlyRemaining < 0 ? "API —" :
        _nexusDailyRemaining == 0
            ? $"API  {_nexusHourlyRemaining:N0}/h  (daily exhausted)"
            : $"API  {_nexusHourlyRemaining:N0}h · {_nexusDailyRemaining:N0}d";
    public string NexusRateLimitColor   =>
        (_nexusHourlyRemaining >= 0 && _nexusHourlyRemaining <= 10) ||
        (_nexusDailyRemaining  >= 0 && _nexusDailyRemaining  <= 100) ? "#c42b2b" :
        (_nexusHourlyRemaining >= 0 && _nexusHourlyRemaining <= 50)  ||
        (_nexusDailyRemaining  >= 0 && _nexusDailyRemaining  <= 500) ? "#e07000" :
        "#888888";

    private void OnNexusRateLimitsUpdated(int hourly, int daily)
    {
        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            _nexusHourlyRemaining = hourly;
            _nexusDailyRemaining  = daily;
            OnPropertyChanged(nameof(NexusRateLimitText));
            OnPropertyChanged(nameof(NexusRateLimitColor));
            OnPropertyChanged(nameof(HasNexusRateLimitData));
        });
    }

    private bool _rateLimitChecking;
    public bool RateLimitChecking
    {
        get => _rateLimitChecking;
        private set { _rateLimitChecking = value; OnPropertyChanged(); }
    }

    public RelayCommand CheckRateLimitsCommand { get; private set; } = null!;

    private async void ExecuteCheckRateLimits()
    {
        if (_nexusApi == null || RateLimitChecking) return;
        RateLimitChecking = true;
        try   { await _nexusApi.ValidateUserAsync(); }
        catch { /* ignore — UpdateRateLimits fires even on error responses */ }
        finally { RateLimitChecking = false; }
    }

    // === Properties ===

    public ObservableCollection<UpdateEntryViewModel> Entries         { get; }
    public ObservableCollection<UpdateEntryViewModel> ActiveEntries   { get; }
    public ObservableCollection<UpdateEntryViewModel> InactiveEntries { get; }
    public ObservableCollection<UpdateEntryViewModel> SyncEntries     { get; }

    public string ActiveSectionHeader   => $"Active Mods ({ActiveEntries.Count})";
    public string InactiveSectionHeader => $"Inactive Mods ({InactiveEntries.Count})";
    public string SyncSectionHeader     => $"Sync Recommended ({SyncEntries.Count})";
    public bool   HasSyncEntries        => SyncEntries.Count > 0;

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

    /// <summary>
    /// Tri-state master checkbox for the Sync section.
    /// </summary>
    public bool? SyncMasterChecked
    {
        get
        {
            var queued = SyncEntries.Where(e => e.CanBeQueued).ToList();
            if (queued.Count == 0) return false;
            var selected = queued.Count(e => e.IsSelected);
            return selected == 0 ? false : selected == queued.Count ? true : (bool?)null;
        }
        set => SetSyncSelected(value == true);
    }

    private void OnEntrySelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UpdateEntryViewModel.IsSelected)) return;
        if (_suppressMasterNotify) return;
        if (sender is UpdateEntryViewModel vm)
        {
            if (ActiveEntries.Contains(vm))
                OnPropertyChanged(nameof(ActiveMasterChecked));
            else if (InactiveEntries.Contains(vm))
                OnPropertyChanged(nameof(InactiveMasterChecked));
            else
                OnPropertyChanged(nameof(SyncMasterChecked));
        }
    }

    public ICollectionView ActiveEntriesView   { get; }
    public ICollectionView InactiveEntriesView { get; }
    public ICollectionView SyncEntriesView     { get; }

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

    // Coloured summary segments
    private string _summaryBaseText = "";
    public string SummaryBaseText
    {
        get => _summaryBaseText;
        private set => SetField(ref _summaryBaseText, value);
    }

    private string _syncSummaryText = "";
    public string SyncSummaryText
    {
        get => _syncSummaryText;
        private set
        {
            if (SetField(ref _syncSummaryText, value))
                OnPropertyChanged(nameof(SyncSummaryVisible));
        }
    }
    public Visibility SyncSummaryVisible => _syncSummaryText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private string _identifySummaryText = "";
    public string IdentifySummaryText
    {
        get => _identifySummaryText;
        private set
        {
            if (SetField(ref _identifySummaryText, value))
                OnPropertyChanged(nameof(IdentifySummaryVisible));
        }
    }
    public Visibility IdentifySummaryVisible => _identifySummaryText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            SetField(ref _isBusy, value);
            DownloadSelectedCommand.RaiseCanExecuteChanged();
            StopAllCommand.RaiseCanExecuteChanged();
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

    /// <summary>
    /// Opens the side WebView panel from outside the ViewModel (e.g. report flow in code-behind).
    /// Mirrors the internal path so IsWebViewPanelOpen stays consistent with the close button.
    /// </summary>
    public void RequestOpenWebViewPanel() => IsWebViewPanelOpen = true;

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
    public RelayCommand StopAllCommand             { get; }
    public RelayCommand CloseCommand               { get; }
    public RelayCommand RefreshCommand             { get; }
    public RelayCommand CloseWebViewCommand        { get; }
    public RelayCommand SkipCommand                { get; }

    public event Action?                       CloseRequested;
    public event Action?                       LoginRequired;
    public event Action<string, int, int>?     NexusMappingAdded;
    public event Action<UpdateEntryViewModel, IReadOnlyList<NexusModCandidate>>? DisambiguationRequested;

    private void OnDisambiguationRequested(UpdateEntryViewModel vm, IReadOnlyList<NexusModCandidate> candidates)
        => DisambiguationRequested?.Invoke(vm, candidates);

    // === Sort ===

    private enum SortCol { Name, Source, Lock, Author }

    // Active section sort state (independent from Inactive)
    private SortCol _activeSortColumn    = SortCol.Name;
    private bool    _activeSortAscending = true;

    // Inactive section sort state (independent from Active)
    private SortCol _inactiveSortColumn    = SortCol.Name;
    private bool    _inactiveSortAscending = true;

    // Sync section sort state
    private SortCol _syncSortColumn    = SortCol.Name;
    private bool    _syncSortAscending = true;

    public ICollectionView EntriesView { get; }

    // ── Active header texts ──────────────────────────────────────────────────
    public string ActiveNameSortHeader   => _activeSortColumn == SortCol.Name   ? (_activeSortAscending ? "Name ↑"     : "Name ↓")     : "Name ⇅";
    public string ActiveAuthorSortHeader => _activeSortColumn == SortCol.Author ? (_activeSortAscending ? "Author ↑"   : "Author ↓")   : "Author ⇅";
    public string ActiveSourceSortHeader => _activeSortColumn == SortCol.Source ? (_activeSortAscending ? "Source ↑"   : "Source ↓")   : "Source ⇅";
    public string ActiveLockSortHeader   => _activeSortColumn == SortCol.Lock   ? (_activeSortAscending ? "↑" : "↓") : "⇅";

    // ── Inactive header texts ────────────────────────────────────────────────
    public string InactiveNameSortHeader   => _inactiveSortColumn == SortCol.Name   ? (_inactiveSortAscending ? "Name ↑"   : "Name ↓")   : "Name ⇅";
    public string InactiveAuthorSortHeader => _inactiveSortColumn == SortCol.Author ? (_inactiveSortAscending ? "Author ↑" : "Author ↓") : "Author ⇅";
    public string InactiveSourceSortHeader => _inactiveSortColumn == SortCol.Source ? (_inactiveSortAscending ? "Source ↑" : "Source ↓") : "Source ⇅";
    public string InactiveLockSortHeader   => _inactiveSortColumn == SortCol.Lock   ? (_inactiveSortAscending ? "↑" : "↓") : "⇅";

    // ── Sync header texts ─────────────────────────────────────────────────────
    public string SyncNameSortHeader   => _syncSortColumn == SortCol.Name   ? (_syncSortAscending ? "Name ↑"   : "Name ↓")   : "Name ⇅";
    public string SyncAuthorSortHeader => _syncSortColumn == SortCol.Author ? (_syncSortAscending ? "Author ↑" : "Author ↓") : "Author ⇅";
    public string SyncLockSortHeader   => _syncSortColumn == SortCol.Lock   ? (_syncSortAscending ? "↑" : "↓") : "⇅";

    // Keep old names as pass-through so any lingering bindings don't crash
    public string NameSortHeader   => ActiveNameSortHeader;
    public string SourceSortHeader => ActiveSourceSortHeader;

    // ── Active commands ──────────────────────────────────────────────────────
    public RelayCommand SortActiveByNameCommand   { get; }
    public RelayCommand SortActiveByAuthorCommand { get; }
    public RelayCommand SortActiveBySourceCommand { get; }
    public RelayCommand SortActiveByLockCommand   { get; }

    // ── Inactive commands ────────────────────────────────────────────────────
    public RelayCommand SortInactiveByNameCommand   { get; }
    public RelayCommand SortInactiveByAuthorCommand { get; }
    public RelayCommand SortInactiveBySourceCommand { get; }
    public RelayCommand SortInactiveByLockCommand   { get; }

    // ── Sync commands ─────────────────────────────────────────────────────────
    public RelayCommand SortSyncByNameCommand   { get; }
    public RelayCommand SortSyncByAuthorCommand { get; }
    public RelayCommand SelectAllSyncCommand   { get; }
    public RelayCommand DeselectAllSyncCommand { get; }

    // Kept for backward compat
    public RelayCommand SortByNameCommand   => SortActiveByNameCommand;
    public RelayCommand SortBySourceCommand => SortActiveBySourceCommand;

    private void ToggleSortActive(SortCol col)
    {
        if (_activeSortColumn == col) _activeSortAscending = !_activeSortAscending;
        else { _activeSortColumn = col; _activeSortAscending = true; }
        ApplySortToView(ActiveEntriesView, _activeSortColumn, _activeSortAscending);
        OnPropertyChanged(nameof(ActiveNameSortHeader));
        OnPropertyChanged(nameof(ActiveAuthorSortHeader));
        OnPropertyChanged(nameof(ActiveSourceSortHeader));
        OnPropertyChanged(nameof(ActiveLockSortHeader));
    }

    private void ToggleSortInactive(SortCol col)
    {
        if (_inactiveSortColumn == col) _inactiveSortAscending = !_inactiveSortAscending;
        else { _inactiveSortColumn = col; _inactiveSortAscending = true; }
        ApplySortToView(InactiveEntriesView, _inactiveSortColumn, _inactiveSortAscending);
        OnPropertyChanged(nameof(InactiveNameSortHeader));
        OnPropertyChanged(nameof(InactiveAuthorSortHeader));
        OnPropertyChanged(nameof(InactiveSourceSortHeader));
        OnPropertyChanged(nameof(InactiveLockSortHeader));
    }

    private void ToggleSortSync(SortCol col)
    {
        if (_syncSortColumn == col) _syncSortAscending = !_syncSortAscending;
        else { _syncSortColumn = col; _syncSortAscending = true; }
        ApplySortToView(SyncEntriesView, _syncSortColumn, _syncSortAscending);
        OnPropertyChanged(nameof(SyncNameSortHeader));
        OnPropertyChanged(nameof(SyncAuthorSortHeader));
        OnPropertyChanged(nameof(SyncLockSortHeader));
    }

    private void SetSyncSelected(bool selected)
    {
        _suppressMasterNotify = true;
        try
        {
            foreach (var e in SyncEntries)
                e.IsSelected = selected;
        }
        finally
        {
            _suppressMasterNotify = false;
        }
        OnPropertyChanged(nameof(SyncMasterChecked));
    }

    private static void ApplySortToView(ICollectionView view, SortCol col, bool ascending)
    {
        var dir = ascending ? ListSortDirection.Ascending : ListSortDirection.Descending;
        view.SortDescriptions.Clear();
        // Ambiguous entries always float to the top regardless of sort column.
        view.SortDescriptions.Add(new SortDescription("IsAmbiguous", ListSortDirection.Descending));
        switch (col)
        {
            case SortCol.Name:
                view.SortDescriptions.Add(new SortDescription("SortName", dir));
                break;
            case SortCol.Author:
                view.SortDescriptions.Add(new SortDescription("LocalAuthor", dir));
                view.SortDescriptions.Add(new SortDescription("SortName", ListSortDirection.Ascending));
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
        // Apply initial sort to all sections independently (called once at construction)
        ApplySortToView(ActiveEntriesView,   _activeSortColumn,   _activeSortAscending);
        ApplySortToView(InactiveEntriesView, _inactiveSortColumn, _inactiveSortAscending);
        ApplySortToView(SyncEntriesView,     _syncSortColumn,     _syncSortAscending);
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
    private readonly Dictionary<(int ModId, long FileId), UpdateEntryViewModel> _downloadingByNxmId = new();

    // === Cancel tracking ===
    // Items currently enqueued in the active batch (set during ExecuteDownloadSelected)
    private List<UpdateEntryViewModel>  _activeAutoItems = [];
    // Batch-level CTS: cancelled when the user confirms "cancel all" → stops all queued + in-progress items
    private CancellationTokenSource?    _batchCts;

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
            SyncEntries.Remove(e);
        }

        OnPropertyChanged(nameof(SyncSectionHeader));
        OnPropertyChanged(nameof(HasSyncEntries));
        UpdateSummary();
        RefreshCommand.RaiseCanExecuteChanged();
    }

    // === Entry removal ===

    /// <summary>
    /// Removes an entry from all observable collections (called after user clicks Unlink).
    /// </summary>
    public void RemoveEntry(UpdateEntryViewModel vm)
    {
        vm.PropertyChanged -= OnEntrySelectionChanged;
        Entries.Remove(vm);
        ActiveEntries.Remove(vm);
        InactiveEntries.Remove(vm);
        SyncEntries.Remove(vm);
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

    private void ExecuteStopAll()
    {
        // Cancel batch CTS — linked token in ExecuteDownloadAsync fires OperationCanceledException on active download
        _batchCts?.Cancel();
        // Immediately update visual state for items that haven't started yet
        foreach (var item in _activeAutoItems)
        {
            if (item.Status == UpdateStatus.Pending)
            {
                item.Status     = UpdateStatus.Skipped;
                item.StatusText = "Cancelled";
            }
        }
    }

    private async void ExecuteDownloadSelected()
    {
        try
        {
            // Build in the order currently shown in the UI (respects column sorting),
            // not in the original insertion order of Entries.
            var ordered = ActiveEntriesView  .OfType<UpdateEntryViewModel>()
                   .Concat(InactiveEntriesView.OfType<UpdateEntryViewModel>())
                   .Concat(SyncEntriesView    .OfType<UpdateEntryViewModel>());

            var autoItems = ordered
                .Where(e => e.IsSelected && e.CanAutoDownload && e.IsActionEnabled)
                .ToList();

            var freeItems = ordered
                .Where(e => e.IsSelected && e.IsActionEnabled &&
                            !e.CanAutoDownload && e.HasNexus && !e.IsNexusUnregistered)
                .ToList();

            if (autoItems.Count == 0 && freeItems.Count == 0) return;

            // Auto-download items (mod.io + Nexus Premium) — routed through UnifiedDownloadQueue
            if (autoItems.Count > 0)
            {
                IsBusy           = true;
                _activeAutoItems = autoItems;
                _batchCts        = new CancellationTokenSource();
                var batchCt      = _batchCts.Token;
                var total        = autoItems.Count;
                var done         = 0;
                var completions  = autoItems.Select(_ => new TaskCompletionSource()).ToArray();

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
                                // If batch was cancelled before this item started, skip it silently
                                if (batchCt.IsCancellationRequested)
                                {
                                    entry.Status     = UpdateStatus.Skipped;
                                    entry.StatusText = "Cancelled";
                                    return;
                                }
                                await entry.ExecuteDownloadAsync(
                                    onProgress: p => Services.UnifiedDownloadQueue.Instance.ReportProgress(p),
                                    batchCt:    batchCt);
                            }
                            finally { tcs.TrySetResult(); }
                        }
                    });
                }

                await Task.WhenAll(completions.Select(t => t.Task));
                _activeAutoItems = [];
                _batchCts?.Dispose();
                _batchCts = null;
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
        // modId must match
        if (_currentSlideEntry.NexusModId.HasValue &&
            _currentSlideEntry.NexusModId.Value != url.NexusModId) return;

        // If the entry has a known fileId, the NXM fileId must also match.
        // A mismatch means the user clicked a different variant/file on the same mod page —
        // tracking the wrong entry would mark it as Updated incorrectly.
        if (_currentSlideEntry.NexusFileId > 0 &&
            _currentSlideEntry.NexusFileId != url.NexusFileId)
        {
            Logger.Warn($"NXM queued: fileId mismatch for [{_currentSlideEntry.ModName}] — " +
                        $"expected {_currentSlideEntry.NexusFileId}, got {url.NexusFileId}. Skipping track.");
            return;
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            _downloadingByNxmId[(url.NexusModId, url.NexusFileId)] = _currentSlideEntry;
            _currentSlideEntry.Status = UpdateStatus.Downloading;
            AdvanceSlide();
        });
    }

    private void OnNxmCompleted(NxmQueueItem item, bool success, string? errorReason)
    {
        // first: tracked dict populated by OnNxmQueued (keyed by exact modId+fileId)
        UpdateEntryViewModel? entry = null;
        var key = (item.Url.NexusModId, item.Url.NexusFileId);
        if (_downloadingByNxmId.TryGetValue(key, out var tracked))
        {
            entry = tracked;
            _downloadingByNxmId.Remove(key);
        }
        else
        {
            // fallback: match by modId+fileId first, then modId-only for entries without a known fileId
            entry = Entries.FirstOrDefault(e =>
                        e.NexusModId.HasValue && e.NexusModId.Value == item.Url.NexusModId &&
                        e.NexusFileId == item.Url.NexusFileId)
                ?? Entries.FirstOrDefault(e =>
                        e.NexusModId.HasValue && e.NexusModId.Value == item.Url.NexusModId &&
                        e.NexusFileId == 0);
        }
        if (entry == null) return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            if (entry.Status == UpdateStatus.Downloading || entry.Status == UpdateStatus.Applying)
            {
                entry.Status     = success ? UpdateStatus.Updated : UpdateStatus.Failed;
                entry.StatusText = success ? "Updated" : "Failed";

                // Sync group children — NxmInstaller installs all paks in the archive,
                // so children should reflect the same outcome as the header row.
                foreach (var child in entry.GroupChildren)
                {
                    child.Status     = success ? UpdateStatus.Updated : UpdateStatus.Failed;
                    child.StatusText = success ? "Updated" : "Failed";
                }
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
        NexusApi.RateLimitsUpdated -= OnNexusRateLimitsUpdated;
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
            // Cancel the batch CTS — stops any in-progress download AND prevents queued items from starting
            _batchCts?.Cancel();
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
            // Panel already open — just navigate to the new mod's page.
            // Re-subscribe to OnNxmQueued so row tracking works even when the panel
            // was opened via the browse button (which doesn't subscribe on its own).
            UnifiedDownloadQueue.Instance.OnNxmQueued -= OnNxmQueued;
            UnifiedDownloadQueue.Instance.OnNxmQueued += OnNxmQueued;
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
        if (_downloadingByNxmId.TryGetValue((item.Url.NexusModId, item.Url.NexusFileId), out var tracked))
            entry = tracked;
        else
            entry = Entries.FirstOrDefault(e =>
                        e.NexusModId.HasValue && e.NexusModId.Value == item.Url.NexusModId &&
                        e.NexusFileId == item.Url.NexusFileId)
                ?? Entries.FirstOrDefault(e =>
                        e.NexusModId.HasValue && e.NexusModId.Value == item.Url.NexusModId &&
                        e.NexusFileId == 0);
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
        // Identify entries (ambiguous Nexus pak) are not update targets — counted separately
        var identifyCount = Entries.Count(e => !e.IsSyncRequired && e.IsAmbiguous);
        var updateTotal   = Entries.Count(e => !e.IsSyncRequired && !e.IsAmbiguous);
        var syncCount     = SyncEntries.Count;
        var autoCount     = Entries.Count(e => !e.IsSyncRequired && !e.IsAmbiguous && e.CanAutoDownload && e.IsActionEnabled);
        var doneCount     = Entries.Count(e => e.Status == UpdateStatus.Updated);
        var manualCount   = Entries.Count(e => !e.IsSyncRequired && !e.IsAmbiguous && !e.CanAutoDownload && !e.IsNexusUnregistered);
        var unregCount    = Entries.Count(e => e.IsNexusUnregistered);

        TitleText = updateTotal > 0
            ? $"{updateTotal} Updates Available"
            : syncCount > 0 ? "Sync Recommended" : "All Up To Date";

        var baseParts = new List<string>();
        if (doneCount   > 0) baseParts.Add($"{doneCount} done");
        if (autoCount   > 0) baseParts.Add($"{autoCount} auto-download");
        if (manualCount > 0) baseParts.Add($"{manualCount} manual");
        if (unregCount  > 0) baseParts.Add($"{unregCount} Nexus unlinked");

        SummaryBaseText = string.Join(" · ", baseParts);

        SyncSummaryText = syncCount > 0
            ? (baseParts.Count > 0 ? " · " : "") + $"{syncCount} sync-needed"
            : "";

        IdentifySummaryText = identifyCount > 0
            ? (baseParts.Count > 0 || syncCount > 0 ? " · " : "") + $"{identifyCount} identify"
            : "";

        // Keep the legacy combined string in case anything else reads it
        var all = new List<string>(baseParts);
        if (syncCount     > 0) all.Add($"{syncCount} sync-needed");
        if (identifyCount > 0) all.Add($"{identifyCount} identify");
        SummaryText = string.Join(" · ", all);
    }
}
