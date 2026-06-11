using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using BG3ModHelper.Services;

namespace BG3ModHelper.Views;

public partial class ModTroubleshootWindow : Window
{
    private static readonly Regex _nexusUrlRegex =
        new(@"/mods/(\d+)", RegexOptions.Compiled);

    private readonly NexusIdDatabase       _db = new();
    private          List<ModSearchResult> _allLocalMods = [];
    private          List<ModSearchResult> _results      = [];
    private          ModSearchResult?      _selected;

    public ModTroubleshootWindow()
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height - 20;
        _db.LoadFromDisk();
        Loaded += (_, _) => { _allLocalMods = BuildLocalModList(); PopulateList(_allLocalMods); };
    }

    // ── Local mod list ───────────────────────────────────────────────────────

    private List<ModSearchResult> BuildLocalModList()
    {
        var modIds = new HashSet<int>();

        var linkStore = new UserModLinkStore();
        foreach (var (_, entry) in linkStore.GetAllNexusLinks())
            if (entry.ModId > 0) modIds.Add(entry.ModId);

        var fileIdStore = new ModFileIdStore();
        fileIdStore.Load();
        foreach (var (_, entry) in fileIdStore.GetAll())
            if (entry.NexusModId > 0) modIds.Add(entry.NexusModId);

        var historyStore = new DownloadHistoryStore();
        historyStore.Load();
        foreach (var entry in historyStore.GetAll())
        {
            if (string.IsNullOrEmpty(entry.HistoryPageUrl)) continue;
            var m = _nexusUrlRegex.Match(entry.HistoryPageUrl);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var id) && id > 0)
                modIds.Add(id);
        }

        return modIds
            .Select(id => _db.GetModById(id) ?? new ModSearchResult(id, "", "", []))
            .OrderBy(r => string.IsNullOrEmpty(r.ModName))   // named mods first
            .ThenBy(r => r.ModName)
            .ThenBy(r => r.ModId)
            .ToList();
    }

    // ── Search / filter ──────────────────────────────────────────────────────

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;

        var query = SearchBox.Text.Trim();

        if (string.IsNullOrEmpty(query))
        {
            PopulateList(_allLocalMods);
            return;
        }

        // URL or plain number → match exact mod ID
        var urlMatch    = _nexusUrlRegex.Match(query);
        int? resolvedId =
            urlMatch.Success && int.TryParse(urlMatch.Groups[1].Value, out var uid) ? uid :
            int.TryParse(query, out var nid)                                        ? nid :
            (int?)null;

        if (resolvedId.HasValue)
        {
            var filtered = _allLocalMods.Where(r => r.ModId == resolvedId.Value).ToList();
            // ID entered explicitly but not in local list → still allow (data may exist)
            if (filtered.Count == 0)
            {
                var result = _db.GetModById(resolvedId.Value)
                             ?? new ModSearchResult(resolvedId.Value, "", "", []);
                filtered = [result];
            }
            PopulateList(filtered);
            return;
        }

        // Text → filter by mod name or pak filename
        var q      = query.ToLowerInvariant();
        var qNoPak = q.EndsWith(".pak") ? q[..^4] : q;
        PopulateList(_allLocalMods.Where(r =>
            r.ModName.ToLowerInvariant().Contains(q) ||
            r.UploadedBy.ToLowerInvariant().Contains(q) ||
            r.PakFileNames.Any(p => p.ToLowerInvariant().Contains(qNoPak))).ToList());
    }

    private void PopulateList(List<ModSearchResult> list)
    {
        _results = list;
        ResultsList.Items.Clear();
        PreviewBorder.Visibility = Visibility.Hidden;
        ClearBtn.IsEnabled = false;
        _selected = null;

        if (_results.Count == 0)
        {
            NoResultsText.Text       = _allLocalMods.Count == 0
                ? "No local mod records found."
                : "No mods found matching that query.";
            NoResultsText.Visibility = Visibility.Visible;
            return;
        }

        NoResultsText.Visibility = Visibility.Hidden;
        foreach (var r in _results)
            ResultsList.Items.Add(r.DisplayLabel);

        if (_results.Count == 1)
            ResultsList.SelectedIndex = 0;
    }

    // ── Selection ────────────────────────────────────────────────────────────

    private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = ResultsList.SelectedIndex;
        if (idx < 0 || idx >= _results.Count)
        {
            _selected = null;
            ClearBtn.IsEnabled = false;
            PreviewBorder.Visibility = Visibility.Hidden;
            return;
        }

        _selected = _results[idx];

        var linkStore    = new UserModLinkStore();
        var fileIdStore  = new ModFileIdStore();
        fileIdStore.Load();
        var historyStore = new DownloadHistoryStore();
        historyStore.Load();

        int nLinks   = linkStore.GetAllNexusLinks().Count(l => l.Entry.ModId == _selected.ModId);
        int nFileIds = fileIdStore.GetAll().Count(kvp => kvp.Value.NexusModId == _selected.ModId);
        int nHistory = historyStore.GetAll().Count(entry =>
        {
            if (string.IsNullOrEmpty(entry.HistoryPageUrl)) return false;
            var m = _nexusUrlRegex.Match(entry.HistoryPageUrl);
            return m.Success && int.TryParse(m.Groups[1].Value, out var id) && id == _selected.ModId;
        });

        int total = nLinks + nFileIds + nHistory;

        if (total == 0)
        {
            PreviewText.Text       = "No local records found for this mod.";
            PreviewText.Foreground = System.Windows.Media.Brushes.Gray;
            ClearBtn.IsEnabled     = false;
        }
        else
        {
            PreviewText.Text       = $"Will delete:  {nLinks} link(s)  ·  {nFileIds} file ID(s)  ·  {nHistory} history entry(s)";
            PreviewText.Foreground = System.Windows.Media.Brushes.Black;
            ClearBtn.IsEnabled     = true;
        }
        PreviewBorder.Visibility = Visibility.Visible;
    }

    // ── Clear ────────────────────────────────────────────────────────────────

    private void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;

        var confirm = MessageBox.Show(
            $"Clear all local records for:\n{_selected.DisplayLabel}\n\n" +
            "The following data will be removed:\n" +
            "  • Mod links  (user_mod_links.json)\n" +
            "  • File ID records  (mod_fileids.json)\n" +
            "  • Download history entries\n\n" +
            "The .pak file will not be deleted.\n\n" +
            "Any previously set identification or sync link for this mod\n" +
            "will be reset — you will need to re-identify or re-sync it\n" +
            "from scratch.\n\nContinue?",
            "Clear Mod Records",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        var linkStore    = new UserModLinkStore();
        var fileIdStore  = new ModFileIdStore();
        fileIdStore.Load();
        var historyStore = new DownloadHistoryStore();
        historyStore.Load();

        int nLinks   = linkStore.RemoveLinksByModId(_selected.ModId);
        int nFileIds = fileIdStore.RemoveByModId(_selected.ModId);
        int nHistory = historyStore.RemoveByModId(_selected.ModId);

        Logger.Info($"ModTroubleshooter: cleared mod {_selected.ModId} — " +
                    $"{nLinks} link(s), {nFileIds} fileId(s), {nHistory} history");

        MessageBox.Show(
            $"Records cleared for {_selected.DisplayLabel}:\n\n" +
            $"  • {nLinks} link(s) removed\n" +
            $"  • {nFileIds} file ID(s) removed\n" +
            $"  • {nHistory} history entry(s) removed\n\n" +
            "Re-identify or re-sync the mod to restore tracking.",
            "Records Cleared",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        // Rebuild list (mod may now have 0 records → remove from list)
        _allLocalMods = BuildLocalModList();
        var currentQuery = SearchBox.Text.Trim();
        SearchBox_TextChanged(this, null!);
    }

    private void ClearAllBtn_Click(object sender, RoutedEventArgs e)
    {
        var linkStore    = new UserModLinkStore();
        var fileIdStore  = new ModFileIdStore();
        fileIdStore.Load();
        var historyStore = new DownloadHistoryStore();
        historyStore.Load();

        int nLinks   = linkStore.GetAllNexusLinks().Count();
        int nFileIds = fileIdStore.GetAll().Count;
        int nHistory = historyStore.GetAll().Count;
        int total    = nLinks + nFileIds + nHistory;

        if (total == 0)
        {
            MessageBox.Show("No local records found.", "Clear All Records",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            "This will permanently remove ALL local tracking records:\n\n" +
            $"  • {nLinks} mod link(s)  (user_mod_links.json)\n" +
            $"  • {nFileIds} file ID record(s)  (mod_fileids.json)\n" +
            $"  • {nHistory} download history entries\n\n" +
            $"Total: {total} record(s) across {_allLocalMods.Count} mod(s).\n\n" +
            "The .pak files will not be deleted.\n\n" +
            "All previously set identification and sync links will be reset.\n" +
            "Every mod will need to be re-identified or re-synced from scratch.\n\n" +
            "This cannot be undone. Continue?",
            "Clear ALL Records",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        int dLinks   = linkStore.ClearAllNexusLinks();
        int dFileIds = fileIdStore.ClearAll();
        historyStore.ClearAll();

        Logger.Info($"ModTroubleshooter: cleared all — {dLinks} links, {dFileIds} fileIds, {nHistory} history");

        MessageBox.Show(
            "All local tracking records have been cleared:\n\n" +
            $"  • {dLinks} link(s) removed\n" +
            $"  • {dFileIds} file ID(s) removed\n" +
            $"  • {nHistory} history entry(s) removed\n\n" +
            "Re-identify or re-sync mods to restore tracking.",
            "All Records Cleared",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        _allLocalMods = BuildLocalModList();
        SearchBox_TextChanged(this, null!);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
