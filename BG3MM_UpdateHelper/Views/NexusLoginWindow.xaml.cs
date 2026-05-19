using System.Windows;
using BG3MM_UpdateHelper.Services;
using Microsoft.Web.WebView2.Core;

namespace BG3MM_UpdateHelper.Views;

public partial class NexusLoginWindow : Window
{
    public bool LoginSuccess { get; private set; }
    private bool _loginDetected;

    public NexusLoginWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitWebViewAsync();
    }

    private async Task InitWebViewAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: WebViewHelper.UserDataFolder);
        await NexusWebView.EnsureCoreWebView2Async(env);
        NexusWebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        NexusWebView.Source = new Uri("https://users.nexusmods.com/auth/sign_in");
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        var url = NexusWebView.Source?.ToString() ?? "";

        if (!_loginDetected && url.Contains("users.nexusmods.com/account/security"))
        {
            _loginDetected = true;
            LoginSuccess   = true;
            WebViewHelper.SetLoggedIn(true);
            Dispatcher.Invoke(() => NexusWebView.Source = new Uri("https://www.nexusmods.com/games/baldursgate3"));
        }
        else if (_loginDetected && url.Contains("nexusmods.com/games/baldursgate3"))
        {
            _ = AcceptCookiesAndCloseAsync();
        }
    }

    private async Task AcceptCookiesAndCloseAsync()
    {
        // 쿠키 배너가 JS로 렌더링될 때까지 최대 3초 대기 후 클릭 시도
        const string script = @"
            (function() {
                var btns = document.querySelectorAll('button');
                for (var b of btns) {
                    var t = (b.textContent || b.innerText || '').toLowerCase().trim();
                    if (t === 'accept all' || t === 'allow all' || t.includes('accept all cookies') || t.includes('allow all cookies')) {
                        b.click(); return true;
                    }
                }
                return false;
            })();
        ";

        for (int i = 0; i < 6; i++)
        {
            await Task.Delay(500);
            var result = await NexusWebView.CoreWebView2.ExecuteScriptAsync(script);
            if (result == "true") break;
        }

        await Task.Delay(300);
        Dispatcher.Invoke(Close);
    }
}
