using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using BG3ModHelper.Models;
using BG3ModHelper.ViewModels;

namespace BG3ModHelper.Views;

public partial class ModIdentifiedListWindow : Window
{
    private readonly ObservableCollection<IdentifiedLinkEntryViewModel> _linkedOnly;

    public ModIdentifiedListWindow(
        IReadOnlyList<UpdateEntryViewModel>        updateListEntries,
        IReadOnlyList<IdentifiedLinkEntryViewModel> linkedOnlyEntries)
    {
        InitializeComponent();

        UpdateListEntries.ItemsSource = updateListEntries;

        _linkedOnly = new ObservableCollection<IdentifiedLinkEntryViewModel>(linkedOnlyEntries);
        LinkedOnlyEntries.ItemsSource = _linkedOnly;

        // Subscribe to ChangeRequested so the View can open the disambiguation dialog
        foreach (var entry in _linkedOnly)
            entry.ChangeRequested += OnLinkedEntryChangeRequested;

        RefreshVisibility(updateListEntries.Count);

        Loaded      += (_, _) => SyncHeaderPadding();
        SizeChanged += (_, _) => SyncHeaderPadding();
    }

    private void OnLinkedEntryChangeRequested(
        IdentifiedLinkEntryViewModel entry,
        IReadOnlyList<NexusModCandidate> candidates)
    {
        var dialog = new ModDisambiguationDialog(entry.PakFileName + ".pak", candidates) { Owner = this };
        dialog.Confirmed += chosen => entry.ApplyChange(chosen);
        dialog.Unlinked  += entry.ApplyUnlink;
        entry.RemoveFromListRequested += vm => _linkedOnly.Remove(vm);
        dialog.Show();
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
