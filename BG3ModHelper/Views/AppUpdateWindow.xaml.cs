using System.Diagnostics;
using System.Windows;
using BG3ModHelper.Services;

namespace BG3ModHelper.Views;

public partial class AppUpdateWindow : Window
{
    public AppUpdateWindow(GitHubUpdateChecker.UpdateInfo info)
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height - 20;

        CurrentVersionText.Text = $"v{info.CurrentVersion}";

        if (info.GitHub is { } gh)
        {
            GitHubVersionText.Text  = $"v{gh.Version}";
            GitHubDateText.Text     = gh.Date;
            GitHubNewBadge.Visibility = gh.IsNewer ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            GitHubVersionText.Text = "—";
        }

        if (info.Nexus is { } nx)
        {
            NexusLabel.Visibility        = Visibility.Visible;
            NexusVersionPanel.Visibility = Visibility.Visible;
            NexusBtn.Visibility          = Visibility.Visible;
            NexusVersionText.Text        = $"v{nx.Version}";
            NexusDateText.Text           = nx.Date;
            NexusNewBadge.Visibility     = nx.IsNewer ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void NexusBtn_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(GitHubUpdateChecker.NexusPageUrl);
        Close();
    }

    private void GitHubBtn_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(GitHubUpdateChecker.ReleasesUrl);
        Close();
    }

    private void LaterBtn_Click(object sender, RoutedEventArgs e) => Close();

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { /* ignore */ }
    }
}
