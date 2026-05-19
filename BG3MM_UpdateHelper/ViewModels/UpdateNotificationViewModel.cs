using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
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
                vm.PageOpenRequested += OnPageOpenRequested;
                vm.NexusRegistered   += OnNexusRegistered;
                return vm;
            }));

        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        SortByNameCommand   = new RelayCommand(() => ToggleSort(SortCol.Name));
        SortBySourceCommand = new RelayCommand(() => ToggleSort(SortCol.Source));
        ApplySort();

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

        // OnCompleted/OnProgress는 창이 열려있는 동안 항상 유지
        UnifiedDownloadQueue.Instance.OnNxmCompleted += OnNxmCompleted;
        UnifiedDownloadQueue.Instance.OnNxmProgress  += OnNxmProgress;

        UpdateSummary();
    }

    // === Properties ===

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

    public RelayCommand SelectAllCommand        { get; }
    public RelayCommand DeselectAllCommand      { get; }
    public RelayCommand DownloadSelectedCommand { get; }
    public RelayCommand CloseCommand            { get; }
    public RelayCommand RefreshCommand          { get; }
    public RelayCommand CloseWebViewCommand     { get; }
    public RelayCommand SkipCommand             { get; }

    public event Action?                       CloseRequested;
    public event Action?                       LoginRequired;
    public event Action<string, int, int>?     NexusMappingAdded;

    // === Sort ===

    private enum SortCol { Name, Source }
    private SortCol _sortColumn    = SortCol.Name;
    private bool    _sortAscending = true;

    public ICollectionView EntriesView { get; }

    public string NameSortHeader   => _sortColumn == SortCol.Name   ? (_sortAscending ? "Name ▲" : "Name ▼") : "Name ⇅";
    public string SourceSortHeader => _sortColumn == SortCol.Source ? (_sortAscending ? "Source ▲" : "Source ▼") : "Source ⇅";

    public RelayCommand SortByNameCommand   { get; }
    public RelayCommand SortBySourceCommand { get; }

    private void ToggleSort(SortCol col)
    {
        if (_sortColumn == col) _sortAscending = !_sortAscending;
        else { _sortColumn = col; _sortAscending = true; }
        ApplySort();
        OnPropertyChanged(nameof(NameSortHeader));
        OnPropertyChanged(nameof(SourceSortHeader));
    }

    private void ApplySort()
    {
        EntriesView.SortDescriptions.Clear();
        var dir = _sortAscending ? ListSortDirection.Ascending : ListSortDirection.Descending;
        if (_sortColumn == SortCol.Name)
        {
            EntriesView.SortDescriptions.Add(new SortDescription("SortName", dir));
        }
        else
        {
            EntriesView.SortDescriptions.Add(new SortDescription("SortSourceOrder", dir));
            EntriesView.SortDescriptions.Add(new SortDescription("SortName", ListSortDirection.Ascending));
        }
    }

    // === WebView slide state ===

    private Queue<UpdateEntryViewModel>        _nexusFreeQueue  = new();
    private UpdateEntryViewModel?              _currentSlideEntry;
    private int                                _webViewTotal;
    private int                                _webViewProgress;
    // nxm mod ID → entry: tracks which entry corresponds to each nxm download
    private readonly Dictionary<int, UpdateEntryViewModel> _downloadingByNxmId = new();

    // === Refresh ===

    private async void ExecuteRefresh()
    {
        if (_reloadFunc == null || IsBusy) return;

        IsBusy   = true;
        BusyText = "Refreshing...";

        try
        {
            var newUpdates = await _reloadFunc();
            var newDict    = newUpdates.ToDictionary(u => u.MetaUuid);

            // Remove entries no longer in the new update list
            var toRemove = Entries
                .Where(e => !newDict.ContainsKey(e.UUID))
                .ToList();
            foreach (var e in toRemove) Entries.Remove(e);

            // Add new entries
            var existing = Entries.Select(e => e.UUID).ToHashSet();
            foreach (var u in newUpdates.Where(u => !existing.Contains(u.MetaUuid)))
            {
                var vm = new UpdateEntryViewModel(u, _nexusIsPremium, _modsFolder,
                    _backupEnabled, _modioApi, _nexusApi, _fileIdStore, _historyStore);
                vm.DownloadRequested += OnDownloadRequested;
                vm.PageOpenRequested += OnPageOpenRequested;
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
                IsBusy = true;
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
                            await Application.Current.Dispatcher.InvokeAsync(
                                () => BusyText = $"({n}/{total}) {entry.ModName}");
                            try   { await entry.ExecuteDownloadAsync(); }
                            finally { tcs.TrySetResult(); }
                        }
                    });
                }

                await Task.WhenAll(completions.Select(t => t.Task));
                IsBusy   = false;
                BusyText = "";
                UpdateSummary();
                DownloadSelectedCommand.RaiseCanExecuteChanged();
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
        // modId가 일치하거나, 슬라이드 중 현재 페이지에서 발생한 nxm이면 현재 엔트리로 간주
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
        // 1순위: OnNxmQueued에서 등록한 추적 딕셔너리
        UpdateEntryViewModel? entry = null;
        if (_downloadingByNxmId.TryGetValue(item.Url.NexusModId, out var tracked))
        {
            entry = tracked;
            _downloadingByNxmId.Remove(item.Url.NexusModId);
        }
        else
        {
            // 2순위: NexusModId로 검색 (폴백)
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
                    ? $"넥서스 다운로드 실패 — {entry.ModName}\n\nAPI 키와 WebView 로그인 계정이 다릅니다.\n설정에서 현재 로그인된 계정과 동일한 API 키를 입력해주세요."
                    : $"넥서스 다운로드 실패 — {entry.ModName}\n\n{errorReason}";
                MessageBox.Show(msg, "다운로드 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private async void OnDownloadRequested(UpdateEntryViewModel entry)
    {
        try
        {
            // Single-item download via row button
            await entry.ExecuteDownloadAsync();
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
            if (progress.Percent >= 95 && entry.Status == UpdateStatus.Downloading)
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
