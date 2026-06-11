using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using BG3ModHelper.Models;
using BG3ModHelper.Services;
using BG3ModHelper.ViewModels;

namespace BG3ModHelper.Views;

public partial class ModIdentifiedListWindow : Window
{
    private readonly ObservableCollection<IdentifiedLinkEntryViewModel> _linkedOnly;
    private readonly NexusApi? _nexusApi;

    public ModIdentifiedListWindow(
        IReadOnlyList<UpdateEntryViewModel>        updateListEntries,
        IReadOnlyList<IdentifiedLinkEntryViewModel> linkedOnlyEntries,
        NexusApi? nexusApi = null)
    {
        InitializeComponent();

        _nexusApi = nexusApi;

        UpdateListEntries.ItemsSource = updateListEntries;

        _linkedOnly = new ObservableCollection<IdentifiedLinkEntryViewModel>(linkedOnlyEntries);
        LinkedOnlyEntries.ItemsSource = _linkedOnly;

        // Subscribe to ChangeRequested so the View can open the disambiguation dialog
        foreach (var entry in _linkedOnly)
            entry.ChangeRequested += OnLinkedEntryChangeRequested;

        RefreshVisibility(updateListEntries.Count);

        // Clamp to work area so the window never extends off-screen on any resolution.
        Loaded      += (_, _) => { MaxHeight = SystemParameters.WorkArea.Height - 40; SyncHeaderPadding(); };
        SizeChanged += (_, _) => SyncHeaderPadding();
    }

    private async void OnLinkedEntryChangeRequested(
        IdentifiedLinkEntryViewModel entry,
        IReadOnlyList<NexusModCandidate> candidates)
    {
        var (descriptions, unverified) = await FetchFilesDataAsync(_nexusApi, candidates);
        var dialog = new ModDisambiguationDialog(entry.PakFileName + ".pak", candidates, descriptions, unverified) { Owner = this };
        dialog.Confirmed += chosen => entry.ApplyChange(chosen);
        dialog.Unlinked  += entry.ApplyUnlink;
        entry.RemoveFromListRequested += vm => _linkedOnly.Remove(vm);
        dialog.Show();
    }

    private static async Task<(IReadOnlyDictionary<long, string>? descriptions,
                                IReadOnlyList<NexusModCandidate>   unverified)>
        FetchFilesDataAsync(
            NexusApi? nexusApi,
            IReadOnlyList<NexusModCandidate> candidates)
    {
        if (nexusApi == null) return (null, []);

        var descriptions = new Dictionary<long, string>();
        var unverified   = new List<NexusModCandidate>();
        var existingIds  = candidates.Select(c => c.FileId).ToHashSet();
        var modInfo = candidates
            .GroupBy(c => c.ModId)
            .ToDictionary(g => g.Key, g => (g.First().ModName, g.First().Author));

        foreach (var modId in candidates.Select(c => c.ModId).Distinct())
        {
            var data = await nexusApi.GetFilesPageAsync(modId);
            if (data == null) continue;

            foreach (var (k, v) in data.Descriptions)
                descriptions[k] = v;

            if (!modInfo.TryGetValue(modId, out var info)) continue;
            foreach (var file in data.Files)
            {
                if (existingIds.Contains(file.NexusFileId)) continue;
                // Skip archived files (no longer downloadable) and old versions (superseded by newer uploads)
                if (file.NexusFileCategoryName.Equals("ARCHIVED",    StringComparison.OrdinalIgnoreCase)) continue;
                if (file.NexusFileCategoryName.Equals("OLD_VERSION", StringComparison.OrdinalIgnoreCase)) continue;
                unverified.Add(new Models.NexusModCandidate(
                    ModId:       modId,
                    ModName:     info.ModName,
                    Author:      info.Author,
                    FileId:      file.NexusFileId,
                    FileName:    file.NexusFileName,
                    FileVersion: file.NexusFileVersion));
            }
        }

        return (descriptions.Count > 0 ? descriptions : null, unverified);
    }

    private void RefreshVisibility(int updateCount)
    {
        var hasLinked = _linkedOnly.Count > 0;
        LinkedOnlyHeader.Visibility = hasLinked ? Visibility.Visible : Visibility.Collapsed;

        var isEmpty = updateCount == 0 && !hasLinked;
        EmptyText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SyncHeaderPadding()
    {
        if (ListScroller.ViewportWidth <= 0) return;
        var scrollbarWidth = ListScroller.ActualWidth - ListScroller.ViewportWidth;
        if (scrollbarWidth < 0) scrollbarWidth = 0;
        HeaderBorder.Padding = new Thickness(8, 6, 8 + scrollbarWidth, 6);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
