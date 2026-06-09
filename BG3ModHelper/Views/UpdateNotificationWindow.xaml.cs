using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using BG3ModHelper.Models;
using BG3ModHelper.Services;
using BG3ModHelper.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace BG3ModHelper.Views;

public partial class UpdateNotificationWindow : Window
{
    private readonly UpdateNotificationViewModel _vm;
    private readonly AppSettings _settings;
    private double _leftPanelWidth;
    private bool   _suppressZoomEvent;
    private int    _pendingReportModId;   // 0 = none pending

    public UpdateNotificationWindow(UpdateNotificationViewModel vm, AppSettings settings)
    {
        InitializeComponent();
        _vm       = vm;
        _settings = settings;
        DataContext = vm;
        vm.CloseRequested              += Close;
        vm.LoginRequired               += OnLoginRequired;
        vm.PropertyChanged             += OnVmPropertyChanged;
        vm.DisambiguationRequested     += OnDisambiguationRequested;
        vm.ShowIdentifiedListRequested += OnShowIdentifiedList;
        UpdateNexusLoginButton(WebViewHelper.IsLoggedIn());
        WebViewHelper.LoginStateChanged += OnLoginStateChanged;
        Loaded += async (_, _) => await InitWebViewAsync();
        Loaded += (_, _) => SyncHeaderPadding();
        SizeChanged += (_, _) => SyncHeaderPadding();
        SourceInitialized += (_, _) => PositionWindow();
        Closed += (_, _) =>
        {
            WebViewHelper.LoginStateChanged -= OnLoginStateChanged;
            WebViewHelper.UnregisterWebView();
            _vm.OnWindowClosing();
        };
    }

    /// <summary>
    /// Syncs header Border right-padding to the ScrollViewer's actual scrollbar width
    /// so that the * (Name) column resolves to the same width in both header and data rows.
    /// Called on Loaded and SizeChanged to handle DPI and window resize.
    /// </summary>
    private void SyncHeaderPadding()
    {
        SyncHeader(ActiveHeaderBorder,   ActiveScroller);
        SyncHeader(InactiveHeaderBorder, InactiveScroller);
    }

    private static void SyncHeader(System.Windows.Controls.Border header,
                                    System.Windows.Controls.ScrollViewer scroller)
    {
        // Guard: if viewport hasn't been measured yet (e.g. during layout transition when
        // the WebView panel opens), ViewportWidth is 0 → scrollbarWidth = ActualWidth → giant
        // right padding that clips all header text.  Skip and let the next SizeChanged fix it.
        if (scroller.ViewportWidth <= 0) return;

        // scrollbarWidth = actual width taken by the vertical scrollbar
        var scrollbarWidth = scroller.ActualWidth - scroller.ViewportWidth;
        if (scrollbarWidth < 0) scrollbarWidth = 0;
        // Match: left=8, right=8(data padding)+scrollbarWidth
        header.Padding = new Thickness(8, 6, 8 + scrollbarWidth, 6);
    }

    private void PositionWindow()
    {
        // Offset left by half the window width so that when the side panel opens (doubling width),
        // the combined window centers on screen. Uses this.Width so it stays correct
        // regardless of the window's configured width.
        var workArea = SystemParameters.WorkArea;
        var centerX  = workArea.Left + workArea.Width  / 2.0;
        var centerY  = workArea.Top  + workArea.Height / 2.0;

        // Clamp height to the work area so the window never opens taller than the screen
        // (common at high DPI scaling where logical units are smaller than physical pixels).
        if (this.Height > workArea.Height)
            this.Height = workArea.Height;

        Left = centerX - this.Width / 2.0 - this.Width / 2.0;
        Top  = Math.Max(workArea.Top, Math.Min(centerY - this.Height / 2.0, workArea.Bottom - this.Height));
    }

    private async Task InitWebViewAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: WebViewHelper.UserDataFolder);
        await NexusWebView.EnsureCoreWebView2Async(env);
        WebViewHelper.RegisterWebView(NexusWebView.CoreWebView2);
        NexusWebView.CoreWebView2.NavigationStarting   += OnWebViewNavigationStarting;
        NexusWebView.CoreWebView2.NavigationCompleted  += OnWebViewNavigationCompleted;

        // apply saved zoom level
        _suppressZoomEvent = true;
        NexusWebView.ZoomFactor = _settings.WebViewZoom;
        _suppressZoomEvent = false;
        UpdateZoomDisplay(_settings.WebViewZoom);
        NexusWebView.ZoomFactorChanged += OnZoomFactorChanged;
    }

    private void OnWebViewNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true; // block system dialog
            try
            {
                var nxmUrl = NxmUrl.Parse(e.Uri);
                UnifiedDownloadQueue.Instance.EnqueueNxm(nxmUrl, e.Uri);
            }
            catch (Exception ex)
            {
                Logger.Warn($"nxm:// intercept failed: {ex.Message}");
            }
            return;
        }

        // Suppress ZoomFactorChanged that fires when WebView resets zoom on navigation
        _suppressZoomEvent = true;
    }

    private void OnZoomFactorChanged(object? sender, object e)
    {
        if (_suppressZoomEvent) return;
        var zoom = NexusWebView.ZoomFactor;
        // Skip if this matches the programmatically applied value — ZoomFactorChanged
        // fires async after NavigationCompleted sets ZoomFactor, so suppress flag is
        // already cleared by then. Comparing values avoids overwriting wheel changes.
        if (Math.Abs(zoom - _settings.WebViewZoom) < 0.001) return;
        _settings.WebViewZoom = zoom;
        UpdateZoomDisplay(zoom);
        SettingsStore.Save(_settings);
    }

    private void UpdateZoomDisplay(double zoom) =>
        Dispatcher.Invoke(() => WebViewZoomText.Text = $"{(int)Math.Round(zoom * 100)}%");

    private void ZoomIn_Click(object sender, RoutedEventArgs e)  => SetZoom(NexusWebView.ZoomFactor + 0.1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(NexusWebView.ZoomFactor - 0.1);

    private void SetZoom(double zoom)
    {
        zoom = Math.Clamp(zoom, 0.25, 3.0);
        _suppressZoomEvent = true;
        NexusWebView.ZoomFactor = zoom;
        _suppressZoomEvent = false;
        _settings.WebViewZoom = zoom;
        UpdateZoomDisplay(zoom);
        SettingsStore.Save(_settings);
    }

    private async void OnWebViewNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        var url = NexusWebView.Source?.ToString() ?? "";
        if (!url.Contains("nexusmods.com"))
        {
            _suppressZoomEvent = false;
            return;
        }

        // ZoomFactor resets per document, so reapply on every navigation completed
        NexusWebView.ZoomFactor = _settings.WebViewZoom;
        _suppressZoomEvent = false;
        UpdateZoomDisplay(_settings.WebViewZoom);

        // Clear pending report flag — navigation to the mod page is all we need.
        // Auto-selection was removed; the user follows the guide in the disambiguation dialog.
        if (_pendingReportModId > 0 && url.Contains($"/mods/{_pendingReportModId}"))
            _pendingReportModId = 0;

        const string cookieScript = @"
            (function() {
                var btns = document.querySelectorAll('button');
                for (var b of btns) {
                    var t = (b.textContent || b.innerText || '').toLowerCase().trim();
                    if (t === 'accept all' || t === 'allow all' || t.includes('accept all cookies') || t.includes('allow all cookies')) {
                        b.click(); return;
                    }
                }
            })();
        ";
        try { await NexusWebView.CoreWebView2.ExecuteScriptAsync(cookieScript); }
        catch { }

        // auto-scroll to downloads list when entering the files tab
        // only scroll if URL still matches — skip subsequent pages like download popups
        if (url.Contains("tab=files") && url == _vm.WebViewCurrentUrl)
        {
            var urlAtLoad = url;
            await Task.Delay(400);
            // cancel scroll if the page changed during the delay (popup etc.)
            if ((NexusWebView.Source?.ToString() ?? "") != urlAtLoad) return;

            // scroll to the element containing "Main files" text
            const string scrollScript = @"
                (function() {
                    // 1) find element containing 'Main files' text
                    var all = document.querySelectorAll('h2, h3, h4, p, span, div, dt');
                    for (var el of all) {
                        if (el.children.length === 0 &&
                            el.textContent.trim().toLowerCase() === 'main files') {
                            var y = el.getBoundingClientRect().top + window.scrollY - 70;
                            window.scrollTo({ top: Math.max(0, y), behavior: 'smooth' });
                            return;
                        }
                    }
                    // 2) fallback: scroll to the actual download link position
                    var dlLink = document.querySelector('a[data-link-id]') ||
                                 document.querySelector('a[href*=""mod-manager-download""]');
                    if (dlLink) {
                        var parent = dlLink.closest('li') || dlLink.closest('article') ||
                                     dlLink.closest('section') || dlLink.parentElement;
                        if (parent) {
                            var y2 = parent.getBoundingClientRect().top + window.scrollY - 100;
                            window.scrollTo({ top: Math.max(0, y2), behavior: 'smooth' });
                            return;
                        }
                    }
                    // 3) last resort fallback
                    var header = document.querySelector('header') || document.querySelector('nav');
                    var base = header ? (header.offsetHeight + 20) : 0;
                    window.scrollTo({ top: base + 350, behavior: 'smooth' });
                })();
            ";
            try { await NexusWebView.CoreWebView2.ExecuteScriptAsync(scrollScript); }
            catch { }
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UpdateNotificationViewModel.IsWebViewPanelOpen))
        {
            if (_vm.IsWebViewPanelOpen)
                OpenWebViewPanel();
            else
                CloseWebViewPanel();
        }
        else if (e.PropertyName == nameof(UpdateNotificationViewModel.WebViewCurrentUrl))
        {
            NavigateWebView(_vm.WebViewCurrentUrl);
        }
    }

    private void OpenWebViewPanel()
    {
        _leftPanelWidth    = this.Width;          // current window width = left panel
        WebViewPanel.Width = _leftPanelWidth;     // right panel same width → 50:50
        this.Width         = _leftPanelWidth * 2; // expand rightward only
        WebViewPanel.Visibility = Visibility.Visible;
        SizeChanged += OnWindowSizeChanged;
        NavigateWebView(_vm.WebViewCurrentUrl);
    }

    private void CloseWebViewPanel()
    {
        SizeChanged -= OnWindowSizeChanged;
        // Capture actual zoom (wheel changes may not have fired ZoomFactorChanged)
        var zoom = NexusWebView.ZoomFactor;
        if (Math.Abs(zoom - _settings.WebViewZoom) > 0.001)
        {
            _settings.WebViewZoom = zoom;
            SettingsStore.Save(_settings);
            UpdateZoomDisplay(zoom);
        }
        WebViewPanel.Visibility = Visibility.Collapsed;
        this.Width = this.Width / 2;
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (WebViewPanel.Visibility == Visibility.Visible)
            WebViewPanel.Width = e.NewSize.Width * 0.5;
    }

    private void NavigateWebView(string url)
    {
        if (string.IsNullOrEmpty(url) || WebViewPanel.Visibility != Visibility.Visible)
            return;

        try { NexusWebView.Source = new Uri(url); }
        catch (UriFormatException) { }
    }

    private void OnLoginStateChanged(bool loggedIn) =>
        Dispatcher.Invoke(() => UpdateNexusLoginButton(loggedIn));

    private static readonly BitmapImage _nexusIconColour = new(new Uri("pack://application:,,,/Assets/nexus.png"));
    private static readonly BitmapImage _nexusIconGrey   = new(new Uri("pack://application:,,,/Assets/nexus_grey.png"));

    private void UpdateNexusLoginButton(bool loggedIn)
    {
        NexusLoginIcon.Source   = loggedIn ? _nexusIconColour : _nexusIconGrey;
        NexusLoginBtn.ToolTip   = loggedIn ? "Nexus Logout" : "Nexus Login";
    }

    private void OnLoginRequired()
    {
        var win = new NexusLoginWindow { Owner = this };
        win.ShowDialog();
    }

    private void ExternalLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string url)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async void NexusLogin_Click(object sender, RoutedEventArgs e)
    {
        if (WebViewHelper.IsLoggedIn())
        {
            await WebViewHelper.LogoutAsync();
        }
        else
        {
            var win = new NexusLoginWindow { Owner = this };
            win.ShowDialog();
        }
    }

    private void OnShowIdentifiedList()
    {
        var win = new ModIdentifiedListWindow(_vm.IdentifiedEntries, _vm.LinkedOnlyEntries) { Owner = this };
        win.Show();
    }

    private void OnDisambiguationRequested(UpdateEntryViewModel entryVm, IReadOnlyList<NexusModCandidate> candidates)
    {
        var pakFileName = System.IO.Path.GetFileName(entryVm.PakFilePath);
        var dialog = new ModDisambiguationDialog(pakFileName, candidates) { Owner = this };
        dialog.ReportAbuseRequested += OnReportAbuseRequested;
        dialog.Confirmed            += entryVm.ApplyDisambiguation;
        dialog.Show();   // non-modal: dialog stays on top of owner but doesn't block interaction
    }

    private void OnReportAbuseRequested(int modId)
    {
        _pendingReportModId = modId;
        var modPageUrl = $"https://www.nexusmods.com/baldursgate3/mods/{modId}";

        // Go through the ViewModel so IsWebViewPanelOpen stays in sync with the close button.
        // Calling OpenWebViewPanel() directly left the flag false → close button had nothing to toggle.
        if (!_vm.IsWebViewPanelOpen)
            _vm.RequestOpenWebViewPanel();

        NavigateWebView(modPageUrl);
    }
}
