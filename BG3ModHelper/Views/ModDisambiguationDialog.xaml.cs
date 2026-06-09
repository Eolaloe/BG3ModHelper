using System.Diagnostics;
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

    public ModDisambiguationDialog(string pakFileName, IReadOnlyList<NexusModCandidate> candidates)
    {
        InitializeComponent();

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
            var candidate = c;
            var radio = new RadioButton { Style = FindResource("CandidateItem") as Style };

            // Row: [text (stretch)] [📋 copy URL] [🚩 report] [↗ open page]
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Text: mod name / author / file info
            var textPanel = new StackPanel();
            textPanel.Children.Add(new TextBlock
            {
                Text       = c.ModName,
                FontWeight = FontWeights.SemiBold,
                FontSize   = 12,
                Foreground = Brushes.Black
            });
            if (!string.IsNullOrEmpty(c.Author))
                textPanel.Children.Add(new TextBlock
                {
                    Text       = $"by {c.Author}",
                    FontSize   = 11,
                    Foreground = lgray,
                    Margin     = new Thickness(0, 1, 0, 0)
                });
            textPanel.Children.Add(new TextBlock
            {
                Text       = $"{c.FileName}  v{c.FileVersion}  (mod {c.ModId} · file {c.FileId})",
                FontSize   = 11,
                Foreground = gray,
                Margin     = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(textPanel, 0);
            row.Children.Add(textPanel);

            var modPageUrl = $"https://www.nexusmods.com/baldursgate3/mods/{candidate.ModId}";

            // 📋 Copy mod page URL to clipboard (col 1)
            var copyBtn = new Button
            {
                Style             = FindResource("LinkButton") as Style,
                ToolTip           = $"Copy mod page URL to clipboard\n{modPageUrl}",
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(8, 0, 0, 0),
                Content           = new TextBlock
                {
                    Text       = "📋",
                    FontSize   = 13,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#555555"))
                }
            };
            copyBtn.Click += (_, _) =>
            {
                try { Clipboard.SetText(modPageUrl); }
                catch { /* ignore */ }
            };
            Grid.SetColumn(copyBtn, 1);
            row.Children.Add(copyBtn);

            // 🚩 Report button (col 2)
            var reportBtn = new Button
            {
                Style             = FindResource("LinkButton") as Style,
                ToolTip           = "Report this mod to Nexus (stolen / duplicate upload)",
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(2, 0, 0, 0),
                Content           = new TextBlock
                {
                    Text       = "🚩",
                    FontSize   = 14,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#cc3300"))
                }
            };
            reportBtn.Click += (_, _) => ReportAbuseRequested?.Invoke(candidate.ModId);
            Grid.SetColumn(reportBtn, 2);
            row.Children.Add(reportBtn);

            // ↗ external link button (col 3)
            var linkBtn = new Button
            {
                Style             = FindResource("LinkButton") as Style,
                ToolTip           = "Open files tab on Nexus",
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(2, 0, 0, 0),
                Content           = new TextBlock
                {
                    Text       = "↗",
                    FontSize   = 17,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0078d4"))
                }
            };
            linkBtn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(candidate.FilesTabUrl) { UseShellExecute = true }); }
                catch { /* ignore */ }
            };
            Grid.SetColumn(linkBtn, 3);
            row.Children.Add(linkBtn);

            radio.Content = row;
            radio.Checked += (_, _) =>
            {
                SelectedCandidate    = candidate;
                ConfirmBtn.IsEnabled = true;
            };

            CandidatePanel.Children.Add(radio);
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
