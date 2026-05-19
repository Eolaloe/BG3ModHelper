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
            store.GetAll().Select(e => new DownloadHistoryEntryViewModel(e)));

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
        foreach (var e in _store.GetAll())
            Entries.Add(new DownloadHistoryEntryViewModel(e));
        OnPropertyChanged(nameof(OldestDateDisplay));
    }
}

/// <summary>Wraps a single DownloadHistoryEntry for display.</summary>
public class DownloadHistoryEntryViewModel
{
    private readonly DownloadHistoryEntry _entry;

    public DownloadHistoryEntryViewModel(DownloadHistoryEntry entry)
    {
        _entry = entry;
        OpenPageCommand = new RelayCommand(OpenPage, () => !string.IsNullOrEmpty(entry.HistoryPageUrl));
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
    public System.Windows.Media.Color SourceBadgeColor => _entry.HistorySource switch
    {
        "Nexus" => System.Windows.Media.Color.FromRgb(0xd9, 0x82, 0x00),
        "ModIO" => System.Windows.Media.Color.FromRgb(0x1a, 0x7f, 0xd4),
        _       => System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)
    };

    public string StatusMark  => _entry.HistorySuccess ? "✓" : "✗";
    public string StatusColor => _entry.HistorySuccess ? "#4caf50" : "#f44336";

    public bool CanOpenPage => !string.IsNullOrEmpty(_entry.HistoryPageUrl);

    // === Command ===

    public RelayCommand OpenPageCommand { get; }

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
