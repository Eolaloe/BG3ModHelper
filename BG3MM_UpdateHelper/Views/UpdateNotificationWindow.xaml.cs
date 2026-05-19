using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using BG3MM_UpdateHelper.Models;
using BG3MM_UpdateHelper.Services;
using BG3MM_UpdateHelper.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace BG3MM_UpdateHelper.Views;

public partial class UpdateNotificationWindow : Window
{
    private readonly UpdateNotificationViewModel _vm;
    private readonly AppSettings _settings;
    private double _leftPanelWidth;
    private bool   _suppressZoomEvent;

    public UpdateNotificationWindow(UpdateNotificationViewModel vm, AppSettings settings)
    {
        InitializeComponent();
        _vm       = vm;
        _settings = settings;
        DataContext = vm;
        vm.CloseRequested  += Close;
        vm.LoginRequired   += OnLoginRequired;
        vm.PropertyChanged += OnVmPropertyChanged;
        UpdateNexusLoginButton(WebViewHelper.IsLoggedIn());
        WebViewHelper.LoginStateChanged += OnLoginStateChanged;
        Loaded += async (_, _) => await InitWebViewAsync();
        SourceInitialized += (_, _) => PositionWindow();
        Closed += (_, _) =>
        {
            WebViewHelper.LoginStateChanged -= OnLoginStateChanged;
            WebViewHelper.UnregisterWebView();
            _vm.OnWindowClosing();
        };
    }

    private void PositionWindow()
    {
        // 화면 중앙에서 좌측으로 400px — 사이드뷰어 열릴 때 전체가 화면 중앙에 오도록
        var workArea = SystemParameters.WorkArea;
        var centerX  = workArea.Left + workArea.Width  / 2.0;
        var centerY  = workArea.Top  + workArea.Height / 2.0;

        Left = centerX - this.Width  / 2.0 - 400;
        Top  = centerY - this.Height / 2.0;
    }

    private async Task InitWebViewAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: WebViewHelper.UserDataFolder);
        await NexusWebView.EnsureCoreWebView2Async(env);
        WebViewHelper.RegisterWebView(NexusWebView.CoreWebView2);
        NexusWebView.CoreWebView2.NavigationStarting   += OnWebViewNavigationStarting;
        NexusWebView.CoreWebView2.NavigationCompleted  += OnWebViewNavigationCompleted;

        // 저장된 배율 적용
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
            e.Cancel = true; // 시스템 다이얼로그 차단
            try
            {
                var nxmUrl = NxmUrl.Parse(e.Uri);
                NxmDownloadQueue.Instance.Enqueue(nxmUrl, e.Uri);
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

        // ZoomFactor는 문서 단위로 리셋되므로 매 로드마다 재적용
        NexusWebView.ZoomFactor = _settings.WebViewZoom;
        _suppressZoomEvent = false;
        UpdateZoomDisplay(_settings.WebViewZoom);

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

        // 파일 탭 진입 시 다운로드 목록으로 자동 스크롤
        // _vm.WebViewCurrentUrl과 일치할 때만 스크롤 — 다운로드 팝업 등 후속 페이지는 제외
        if (url.Contains("tab=files") && url == _vm.WebViewCurrentUrl)
        {
            var urlAtLoad = url;
            await Task.Delay(400);
            // 지연 중 다른 페이지로 이동한 경우(팝업 등) 스크롤 취소
            if ((NexusWebView.Source?.ToString() ?? "") != urlAtLoad) return;

            // "Main files" 텍스트를 가진 요소를 찾아 화면 최상단으로 스크롤
            const string scrollScript = @"
                (function() {
                    // 1) 'Main files' 텍스트를 포함한 요소 탐색
                    var all = document.querySelectorAll('h2, h3, h4, p, span, div, dt');
                    for (var el of all) {
                        if (el.children.length === 0 &&
                            el.textContent.trim().toLowerCase() === 'main files') {
                            var y = el.getBoundingClientRect().top + window.scrollY - 70;
                            window.scrollTo({ top: Math.max(0, y), behavior: 'smooth' });
                            return;
                        }
                    }
                    // 2) 폴백: 실제 다운로드 링크 위치로
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
                    // 3) 최종 폴백
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
        _leftPanelWidth    = this.Width;          // 현재 창 너비 = 왼쪽 패널
        WebViewPanel.Width = _leftPanelWidth;     // 오른쪽도 동일 → 50:50
        this.Width         = _leftPanelWidth * 2; // 오른쪽으로만 확장
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

    private static readonly BitmapImage _nexusIconColour = new(new Uri("pack://application:,,,/nexus.png"));
    private static readonly BitmapImage _nexusIconGrey   = new(new Uri("pack://application:,,,/nexus_grey.png"));

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
}
