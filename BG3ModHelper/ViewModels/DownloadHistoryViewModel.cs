using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using BG3ModHelper.Models;
using BG3ModHelper.Services;

namespace BG3ModHelper.ViewModels;

/// <summary>ViewModel for the Download History popup window.</summary>
public class DownloadHistoryViewModel : ViewModelBase
{
    private readonly DownloadHistoryStore _store;

    public DownloadHistoryViewModel(DownloadHistoryStore store)
    {
        _store = store;
        Entries = new ObservableCollection<DownloadHistoryEntryViewModel>(
            GroupEntries(store.GetAll().Select(e => new DownloadHistoryEntryViewModel(e)).ToList()));

        CloseCommand              = new RelayCommand(() => CloseRequested?.Invoke());
        ClearAllCommand           = new RelayCommand(() => ExecuteClear(null));
        ClearExcept1DayCommand    = new RelayCommand(() => ExecuteClear(1));
        ClearExcept1WeekCommand   = new RelayCommand(() => ExecuteClear(7));
        ClearExcept1MonthCommand  = new RelayCommand(() => ExecuteClear(30));
        ClearExcept1YearCommand   = new RelayCommand(() => ExecuteClear(365));
    }

    public ObservableCollection<DownloadHistoryEntryViewModel> Entries { get; }

    public string OldestDateDisplay
    {
        get
        {
            var all = _store.GetAll();
            return all.Count > 0
                ? $"Oldest: {all[^1].HistoryDownloadedAt.ToLocalTime():yyyy-MM-dd}"
                : "No history";
        }
    }

    public RelayCommand CloseCommand             { get; }
    public RelayCommand ClearAllCommand          { get; }
    public RelayCommand ClearExcept1DayCommand   { get; }
    public RelayCommand ClearExcept1WeekCommand  { get; }
    public RelayCommand ClearExcept1MonthCommand { get; }
    public RelayCommand ClearExcept1YearCommand  { get; }

    public event Action? CloseRequested;

    private static IEnumerable<DownloadHistoryEntryViewModel> GroupEntries(
        IReadOnlyList<DownloadHistoryEntryViewModel> vms)
    {
        var children = new HashSet<DownloadHistoryEntryViewModel>();

        var groups = vms
            .Where(vm => vm.GroupId != null)
            .GroupBy(vm => vm.GroupId!)
            .Where(g => g.Count() > 1);

        foreach (var g in groups)
        {
            var header = g.First();
            foreach (var child in g.Skip(1))
            {
                header.AddGroupChild(child);
                children.Add(child);
            }
        }

        return vms.Where(vm => !children.Contains(vm));
    }

    private void ExecuteClear(int? keepDays)
    {
        var label = keepDays switch
        {
            null => null,
            1    => "1 day",
            7    => "1 week",
            30   => "1 month",
            365  => "1 year",
            _    => $"{keepDays} days"
        };
        var desc = label == null
            ? "Clear all download history?"
            : $"Clear history older than {label}?";

        var result = MessageBox.Show(desc, "Clear History",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        if (keepDays == null)
            _store.ClearAll();
        else
            _store.ClearBefore(DateTime.UtcNow.AddDays(-keepDays.Value));

        Entries.Clear();
        foreach (var e in GroupEntries(_store.GetAll().Select(e => new DownloadHistoryEntryViewModel(e)).ToList()))
            Entries.Add(e);
        OnPropertyChanged(nameof(OldestDateDisplay));
    }
}

/// <summary>Wraps a single DownloadHistoryEntry for display.</summary>
public class DownloadHistoryEntryViewModel : ViewModelBase
{
    private readonly DownloadHistoryEntry _entry;
    private readonly List<DownloadHistoryEntryViewModel> _groupChildren = [];
    private bool _isGroupExpanded;

    public DownloadHistoryEntryViewModel(DownloadHistoryEntry entry)
    {
        _entry = entry;
        OpenPageCommand      = new RelayCommand(OpenPage, () => !string.IsNullOrEmpty(entry.HistoryPageUrl));
        ToggleExpandCommand  = new RelayCommand(() => IsGroupExpanded = !IsGroupExpanded, () => HasGroupChildren);
    }

    // === Group support ===

    public string? GroupId          => _entry.HistoryGroupId;
    public IReadOnlyList<DownloadHistoryEntryViewModel> GroupChildren    => _groupChildren;
    public bool                                         HasGroupChildren => _groupChildren.Count > 0;

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

    internal void AddGroupChild(DownloadHistoryEntryViewModel child)
    {
        _groupChildren.Add(child);
        OnPropertyChanged(nameof(GroupChildren));
        OnPropertyChanged(nameof(HasGroupChildren));
    }

    // === Display ===

    public string DateDisplay =>
        _entry.HistoryDownloadedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string ModName         => _entry.HistoryModName;
    public string PlatformModName => _entry.HistoryPlatformModName;
    public string LocalModName    => _entry.HistoryModName;
    public bool   HasPlatformName => !string.IsNullOrEmpty(_entry.HistoryPlatformModName);

    public string FromVersion => _entry.HistoryFromVersion;
    public string ToVersion   => _entry.HistoryToVersion;

    /// <summary>Source label for badge display.</summary>
    public string SourceDisplay => _entry.HistorySource switch
    {
        "ModIO"  => "mod.io",
        "Nxm"    => "nxm",
        "Others" => "Others",
        _        => _entry.HistorySource
    };

    /// <summary>Badge background color — matches UpdateEntryViewModel.SourceBadgeColor.</summary>
    public string SourceBadgeColor => _entry.HistorySource switch
    {
        "Nexus" => "#d98200",
        "ModIO" => "#1a7fd4",
        _       => "#888888"
    };

    public bool   WasRenamed   => !string.IsNullOrEmpty(_entry.HistoryReplacedPakFileName);
    public string StatusMark  => WasRenamed ? "↺" : (_entry.HistorySuccess ? "✓" : "✗");
    public string StatusColor => WasRenamed ? "#ffc107" : (_entry.HistorySuccess ? "#4caf50" : "#f44336");

    /// <summary>
    /// Tooltip shown on the status mark for all entries.
    /// Shows which pak file(s) changed and the version transition.
    /// </summary>
    public string StatusTooltip
    {
        get
        {
            // Rename: show old pak filename → new pak filename
            if (WasRenamed)
                return $"{_entry.HistoryReplacedPakFileName}\n→  {_entry.HistoryPakFileName ?? _entry.HistoryModName}";

            // Normal install/update: show the installed pak filename
            var pak = _entry.HistoryPakFileName;
            if (string.IsNullOrEmpty(pak)) pak = _entry.HistoryModName;
            return !_entry.HistorySuccess ? $"{pak}\n(Download failed)" : pak;
        }
    }

    public bool CanOpenPage => !string.IsNullOrEmpty(_entry.HistoryPageUrl);

    // === Commands ===

    public RelayCommand OpenPageCommand     { get; }
    public RelayCommand ToggleExpandCommand { get; }

    private void OpenPage()
    {
        if (string.IsNullOrEmpty(_entry.HistoryPageUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo(_entry.HistoryPageUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open URL: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
