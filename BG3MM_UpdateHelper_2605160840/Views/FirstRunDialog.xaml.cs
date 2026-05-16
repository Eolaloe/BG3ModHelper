using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using BG3MM_UpdateHelper.Models;
using BG3MM_UpdateHelper.Services;
using Microsoft.Win32;

namespace BG3MM_UpdateHelper.Views;

public partial class FirstRunDialog : Window
{
    public AppSettings? Result { get; private set; }

    public FirstRunDialog()
    {
        InitializeComponent();
        UpdateStartButton();
    }

    // ── Input validation ──────────────────────────────────────────────────

    private void OnInputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => UpdateStartButton();

    private void UpdateStartButton()
    {
        var hasBg3mm   = !string.IsNullOrWhiteSpace(BG3MMPathBox.Text);
        var hasNexus   = !string.IsNullOrWhiteSpace(NexusKeyBox.Text);
        var hasModio   = !string.IsNullOrWhiteSpace(ModioKeyBox.Text);
        var hasAnyKey  = hasNexus || hasModio;

        // BG3MM 경로 배지
        BG3MMBadge.Visibility = hasBg3mm ? Visibility.Collapsed : Visibility.Visible;

        // API 키 배지
        ApiKeyBadge.Visibility = hasAnyKey ? Visibility.Collapsed : Visibility.Visible;
        ApiKeyHint.Visibility  = hasAnyKey ? Visibility.Collapsed : Visibility.Visible;

        // Get Started 활성화 조건: BG3MM 경로 + 최소 1개 API 키
        StartButton.IsEnabled = hasBg3mm && hasAnyKey;
    }

    // ── Browse BG3MM folder ───────────────────────────────────────────────

    private void BrowseBG3MM_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = $"Select the folder containing {Constants.BG3MM_EXE_NAME}"
        };

        if (dialog.ShowDialog() == true)
        {
            BG3MMPathBox.Text = dialog.FolderName;
            UpdateStartButton();
        }
    }

    // ── API key pages ─────────────────────────────────────────────────────

    private void OpenNexusKeyPage_Click(object sender, RoutedEventArgs e) =>
        OpenUrl(Constants.NEXUS_API_KEY_HELP_URL);

    private void OpenModioKeyPage_Click(object sender, RoutedEventArgs e) =>
        OpenUrl(Constants.MODIO_API_KEY_HELP_URL);

    // ── Nexus login ───────────────────────────────────────────────────────

    private async void NexusLogin_Click(object sender, RoutedEventArgs e)
    {
        NexusLoginButton.IsEnabled = false;
        NexusLoginStatus.Text      = "Opening browser...";

        try
        {
            var history = await NexusWebLogin.LoginAndFetchHistoryAsync(this);
            if (history != null)
            {
                NexusLoginStatus.Text     = $"✓ Linked  ({history.Count} mods in history)";
                NexusLoginStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x2e, 0x7d, 0x32));
                NexusLoginButton.Content  = "Re-link Account";
            }
            else
            {
                NexusLoginStatus.Text = "Cancelled";
            }
        }
        catch (Exception ex)
        {
            NexusLoginStatus.Text = "Failed — try again";
            Logger.Warn($"FirstRun Nexus login failed: {ex.Message}");
        }
        finally
        {
            NexusLoginButton.IsEnabled = true;
        }
    }

    private void SkipNexus_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Without Nexus account linking, Nexus-only mods cannot be auto-matched.\n\n" +
            "You can link your account later in Settings.\n\nSkip anyway?",
            "Skip Nexus Account?",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            NexusLoginStatus.Text = "Skipped";
            SkipNexusButton.Visibility = Visibility.Collapsed;
        }
    }

    // ── Finish ────────────────────────────────────────────────────────────

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        var bg3mmPath = BG3MMPathBox.Text.Trim();

        if (!PathDiscovery.IsValidBG3MMFolder(bg3mmPath))
        {
            var answer = MessageBox.Show(
                $"{Constants.BG3MM_EXE_NAME} was not found in the selected folder.\n\n" +
                "Continue anyway?",
                "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        Result = new AppSettings
        {
            BG3MMFolderPath = bg3mmPath,
            NexusAPIKey     = NexusKeyBox.Text.Trim(),
            ModioAPIKey     = ModioKeyBox.Text.Trim(),
        };

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"Failed to open URL: {ex.Message}"); }
    }
}
