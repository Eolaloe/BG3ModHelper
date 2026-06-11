using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BG3ModHelper.Models;

namespace BG3ModHelper.Views;

public partial class ModDisambiguationDialog : Window
{
    public NexusModCandidate? SelectedCandidate { get; private set; }

    /// <summary>Raised when the user confirms a candidate selection.</summary>
    public event Action<NexusModCandidate>? Confirmed;

    /// <summary>Raised when the user clicks Unlink — remove any stored Nexus link for this pak.</summary>
    public event Action? Unlinked;

    /// <summary>Raised when the user clicks the Report (🚩) button for a candidate mod.</summary>
    public event Action<int>? ReportAbuseRequested;

    // Tracks which RadioButtons belong to unverified candidates so we can deselect them on toggle-off.
    private readonly List<RadioButton> _unverifiedRadios = new();

    // Captured on first Loaded — used to compute how much extra space the user has added by resizing.
    private double _defaultWindowHeight;

    /// <param name="descriptions">
    /// Optional FileId → description map pre-fetched before opening this dialog.
    /// Provides hover tooltips (0.2 s delay) on each candidate entry.
    /// </param>
    /// <param name="unverifiedCandidates">
    /// Optional list of uploads not in the DB (content_preview_link broken at upload time).
    /// Shown only when the user explicitly ticks the "Show all uploads" checkbox.
    /// </param>
    public ModDisambiguationDialog(
        string pakFileName,
        IReadOnlyList<NexusModCandidate> candidates,
        IReadOnlyDictionary<long, string>? descriptions        = null,
        IReadOnlyList<NexusModCandidate>?  unverifiedCandidates = null)
    {
        InitializeComponent();

        // Clamp window height to the taskbar-adjusted work area so the dialog
        // never extends off-screen regardless of how many candidates are listed.
        // Capture default height (5-item cap) so SizeChanged can expand the list proportionally.
        Loaded += (_, _) =>
        {
            MaxHeight = SystemParameters.WorkArea.Height - 40;
            _defaultWindowHeight = ActualHeight;
        };
        SizeChanged += (_, _) =>
        {
            if (_defaultWindowHeight <= 0) return;
            var extra = ActualHeight - _defaultWindowHeight;
            MainScroller.MaxHeight = 320 + Math.Max(0, extra);
        };

        SubtitleText.Text =
            $"The pak file \"{pakFileName}\" matches {candidates.Count} different mods on Nexus.\n" +
            "Select the one you actually have installed.";

        var gray  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#666666"));
        var lgray = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#999999"));

        // Primary: ModId ascending (lower = registered earlier = more likely the original)
        // Secondary: FileId descending (higher = newer file upload)
        var sorted = candidates
            .OrderBy(c => c.ModId)
            .ThenByDescending(c => c.FileId)
            .ToList();

        foreach (var c in sorted)
        {
            var radio = BuildCandidateRadio(c, gray, lgray, "CandidateItem", descriptions);
            CandidatePanel.Children.Add(radio);
        }

        // Build unverified RadioButtons upfront; panel stays hidden until checkbox is ticked.
        if (unverifiedCandidates != null && unverifiedCandidates.Count > 0)
        {
            var sortedUnverified = unverifiedCandidates
                .OrderBy(c => c.ModId)
                .ThenByDescending(c => c.FileId)
                .ToList();

            foreach (var c in sortedUnverified)
            {
                var radio = BuildCandidateRadio(c, gray, lgray, "UnverifiedCandidateItem", descriptions);
                _unverifiedRadios.Add(radio);
                UnverifiedPanel.Children.Add(radio);
            }

            ShowUnverifiedCheck.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Builds one candidate RadioButton row (verified or unverified style).</summary>
    private RadioButton BuildCandidateRadio(
        NexusModCandidate candidate,
        Brush gray, Brush lgray,
        string styleKey,
        IReadOnlyDictionary<long, string>? descriptions)
    {
        var radio = new RadioButton { Style = FindResource(styleKey) as Style };

        if (descriptions != null && descriptions.TryGetValue(candidate.FileId, out var desc) &&
            !string.IsNullOrWhiteSpace(desc))
        {
            radio.ToolTip = desc;
            ToolTipService.SetInitialShowDelay(radio, 200);
            ToolTipService.SetShowDuration(radio, 30000);
        }

        // Row: [text (stretch)] [📋 copy URL] [🚩 report] [↗ open page]
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textPanel = new StackPanel();
        textPanel.Children.Add(new TextBlock
        {
            Text       = candidate.ModName,
            FontWeight = FontWeights.SemiBold,
            FontSize   = 12,
            Foreground = Brushes.Black
        });
        if (!string.IsNullOrEmpty(candidate.Author))
            textPanel.Children.Add(new TextBlock
            {
                Text       = $"by {candidate.Author}",
                FontSize   = 11,
                Foreground = lgray,
                Margin     = new Thickness(0, 1, 0, 0)
            });
        textPanel.Children.Add(new TextBlock
        {
            Text       = $"{candidate.FileName}  v{candidate.FileVersion}  (mod {candidate.ModId} · file {candidate.FileId})",
            FontSize   = 11,
            Foreground = gray,
            Margin     = new Thickness(0, 2, 0, 0)
        });
        Grid.SetColumn(textPanel, 0);
        row.Children.Add(textPanel);

        var modPageUrl = $"https://www.nexusmods.com/baldursgate3/mods/{candidate.ModId}";

        // 📋 Copy mod page URL (col 1)
        var copyBtn = new Button
        {
            Style             = FindResource("LinkButton") as Style,
            ToolTip           = $"Copy mod page URL to clipboard\n{modPageUrl}",
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(8, 0, 0, 0),
            Content           = new TextBlock { Text = "📋", FontSize = 13,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#555555")) }
        };
        copyBtn.Click += (_, _) => { try { Clipboard.SetText(modPageUrl); } catch { } };
        Grid.SetColumn(copyBtn, 1);
        row.Children.Add(copyBtn);

        // 🚩 Report (col 2)
        var capturedModId = candidate.ModId;
        var reportBtn = new Button
        {
            Style             = FindResource("LinkButton") as Style,
            ToolTip           = "Report this mod to Nexus (stolen / duplicate upload)",
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(2, 0, 0, 0),
            Content           = new TextBlock { Text = "🚩", FontSize = 14,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#cc3300")) }
        };
        reportBtn.Click += (_, _) => ReportAbuseRequested?.Invoke(capturedModId);
        Grid.SetColumn(reportBtn, 2);
        row.Children.Add(reportBtn);

        // ↗ Open files tab (col 3)
        var capturedUrl = candidate.FilesTabUrl;
        var linkBtn = new Button
        {
            Style             = FindResource("LinkButton") as Style,
            ToolTip           = "Open files tab on Nexus",
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(2, 0, 0, 0),
            Content           = new TextBlock { Text = "↗", FontSize = 17,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0078d4")) }
        };
        linkBtn.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(capturedUrl) { UseShellExecute = true }); }
            catch { }
        };
        Grid.SetColumn(linkBtn, 3);
        row.Children.Add(linkBtn);

        radio.Content = row;

        var capturedCandidate = candidate;
        radio.Checked += (_, _) =>
        {
            SelectedCandidate    = capturedCandidate;
            ConfirmBtn.IsEnabled = true;
        };

        return radio;
    }

    private void ShowUnverifiedCheck_Checked(object sender, RoutedEventArgs e)
    {
        UnverifiedSection.Visibility = Visibility.Visible;
        // Scroll to top so the unverified entries are immediately visible without scrolling
        MainScroller.ScrollToTop();
    }

    private void ShowUnverifiedCheck_Unchecked(object sender, RoutedEventArgs e)
    {
        UnverifiedSection.Visibility = Visibility.Collapsed;

        // If an unverified candidate was selected, deselect it
        if (_unverifiedRadios.Any(r => r.IsChecked == true))
        {
            foreach (var r in _unverifiedRadios) r.IsChecked = false;
            SelectedCandidate    = null;
            ConfirmBtn.IsEnabled = false;
        }
    }

    private async void CopyDetailsChip_Click(object sender, MouseButtonEventArgs e)
    {
        const string text = "Unauthorized reupload. Redistributed without the original author's permission.";
        try { Clipboard.SetText(text); }
        catch { return; }

        CopyDetailsIcon.Text = "✓";
        await Task.Delay(1500);
        if (IsLoaded) CopyDetailsIcon.Text = "📋";
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCandidate == null) return;
        Confirmed?.Invoke(SelectedCandidate);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Unlink_Click(object sender, RoutedEventArgs e)
    {
        Unlinked?.Invoke();
        Close();
    }
}
