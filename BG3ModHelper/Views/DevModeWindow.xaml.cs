using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BG3ModHelper.Services;

namespace BG3ModHelper.Views;

public partial class DevModeWindow : Window
{
    private readonly DispatcherTimer _autoRefresh;

    public DevModeWindow()
    {
        InitializeComponent();
        SkipChangelogCheck.IsChecked = DevMode.SkipChangelog;
        RefreshStats();

        _autoRefresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _autoRefresh.Tick += (_, _) => RefreshStats();
        _autoRefresh.Start();
        Closed += (_, _) => _autoRefresh.Stop();

        // Close when the main app window closes
        var mainWin = Application.Current?.MainWindow;
        if (mainWin != null)
        {
            void onMainClosed(object? s, EventArgs e) => Close();
            mainWin.Closed += onMainClosed;
            Closed += (_, _) => mainWin.Closed -= onMainClosed;
        }
    }

    private void SkipChangelogCheck_Changed(object sender, RoutedEventArgs e)
    {
        DevMode.SkipChangelog = SkipChangelogCheck.IsChecked ?? false;
        Logger.Info($"[DevMode] SkipChangelog = {DevMode.SkipChangelog}");
    }

    private void RefreshStats_Click(object sender, RoutedEventArgs e) => RefreshStats();

    private void ResetStats_Click(object sender, RoutedEventArgs e)
    {
        ApiCallTracker.BeginSession();
        RefreshStats();
    }

    private void RefreshStats()
    {
        // ── Summary row ─────────────────────────────────────────────────────
        TotalCallsText.Text = ApiCallTracker.TotalCalls.ToString();

        var (hourlyUsed, dailyUsed) = ApiCallTracker.GetRateLimitDelta();
        HourlyUsedText.Text = hourlyUsed >= 0 ? hourlyUsed.ToString() : "—";
        DailyUsedText.Text  = dailyUsed  >= 0 ? dailyUsed.ToString()  : "—";

        var h = NexusApi.LastKnownHourlyRemaining;
        var d = NexusApi.LastKnownDailyRemaining;
        HourlyRemText.Text = h >= 0 ? h.ToString() : "—";
        DailyRemText.Text  = d >= 0 ? d.ToString() : "—";

        // ── Per-category breakdown ───────────────────────────────────────────
        StatsPanel.Children.Clear();
        var rows = ApiCallTracker.GetSnapshot();

        if (rows.Count == 0)
        {
            StatsBorder.Visibility  = Visibility.Collapsed;
            NoStatsText.Visibility  = Visibility.Visible;
            return;
        }

        StatsBorder.Visibility = Visibility.Visible;
        NoStatsText.Visibility = Visibility.Collapsed;

        var total = rows.Sum(r => r.Count);
        var isFirst = true;

        foreach (var (category, count) in rows)
        {
            var row = new Border
            {
                BorderBrush     = new SolidColorBrush(Color.FromRgb(0xe0, 0xe0, 0xe0)),
                BorderThickness = isFirst ? new Thickness(0) : new Thickness(0, 1, 0, 0),
                Padding         = new Thickness(8, 5, 8, 5),
            };
            isFirst = false;

            var pct   = total > 0 ? (double)count / total : 0;
            var grid  = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Category label
            var label = new TextBlock
            {
                Text     = category,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            // Count + percentage
            var countText = new TextBlock
            {
                Text      = $"{count}  ({pct:P0})",
                FontSize  = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(countText, 1);
            grid.Children.Add(countText);

            row.Child = grid;
            StatsPanel.Children.Add(row);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
