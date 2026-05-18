using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using BG3MM_UpdateHelper.Models;
using BG3MM_UpdateHelper.Services;

namespace BG3MM_UpdateHelper.ViewModels;

/// <summary>ViewModel for the Download History popup window.</summary>
public class DownloadHistoryViewModel : ViewModelBase
{
    public DownloadHistoryViewModel(DownloadHistoryStore store)
    {
        Entries = new ObservableCollection<DownloadHistoryEntryViewModel>(
            store.GetAll().Select(e => new DownloadHistoryEntryViewModel(e)));

        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());
    }

    public ObservableCollection<DownloadHistoryEntryViewModel> Entries { get; }
    public RelayCommand CloseCommand { get; }

    public event Action? CloseRequested;
}

/// <summary>Wraps a single DownloadHistoryEntry for display.</summary>
public class DownloadHistoryEntryViewModel
{
    private readonly DownloadHistoryEntry _entry;

    public DownloadHistoryEntryViewModel(DownloadHistoryEntry entry)
    {
        _entry = entry;
        OpenPageCommand = new RelayCommand(OpenPage, () => !string.IsNullOrEmpty(entry.PageUrl));
    }

    // === Display ===

    public string DateDisplay =>
        _entry.DownloadedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string ModName => _entry.ModName;

    public string FromVersion => _entry.FromVersion;
    public string ToVersion   => _entry.ToVersion;

    /// <summary>Source label for badge display.</summary>
    public string SourceDisplay => _entry.Source switch
    {
        "ModIO"  => "mod.io",
        "Nxm"    => "nxm",
        "Others" => "Others",
        _        => _entry.Source  // "Nexus" 그대로
    };

    /// <summary>Badge background color — matches UpdateEntryViewModel.SourceBadgeColor.</summary>
    public System.Windows.Media.Color SourceBadgeColor => _entry.Source switch
    {
        "Nexus" => System.Windows.Media.Color.FromRgb(0xd9, 0x82, 0x00),
        "ModIO" => System.Windows.Media.Color.FromRgb(0x1a, 0x7f, 0xd4),
        _       => System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)
    };

    public string StatusMark  => _entry.Success ? "✓" : "✗";
    public string StatusColor => _entry.Success ? "#4caf50" : "#f44336";

    public bool CanOpenPage => !string.IsNullOrEmpty(_entry.PageUrl);

    // === Command ===

    public RelayCommand OpenPageCommand { get; }

    private void OpenPage()
    {
        if (string.IsNullOrEmpty(_entry.PageUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo(_entry.PageUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open URL: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
