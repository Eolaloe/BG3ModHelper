using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json.Linq;

namespace BG3MM_UpdateHelper.Services;

public static class NexusWebLogin
{
    private const string NEXUS_LOGIN   = "https://users.nexusmods.com/";
    private const string NEXUS_HOME    = "https://www.nexusmods.com";
    private const string LOGGED_IN_URL = "https://www.nexusmods.com/";
    private const string HISTORY_URL   =
        "https://www.nexusmods.com/Core/Libs/Common/Managers/Mods?GetDownloadHistory";

    private static readonly string WebViewDataPath =
        System.IO.Path.Combine(SettingsStore.GetDataFolder(), "WebView2Cache");

    public static async Task<List<NexusDownloadEntry>?> LoginAndFetchHistoryAsync(Window owner)
    {
        var tcs    = new TaskCompletionSource<List<NexusDownloadEntry>?>();
        var window = new NexusLoginWindow(owner, tcs);
        window.ShowDialog();
        return await tcs.Task;
    }

    private class NexusLoginWindow : Window
    {
        private readonly WebView2 _webView = new();
        private readonly TaskCompletionSource<List<NexusDownloadEntry>?> _tcs;
        private bool _fetchStarted;
        private bool _cameFromUsers;

        public NexusLoginWindow(Window owner, TaskCompletionSource<List<NexusDownloadEntry>?> tcs)
        {
            _tcs  = tcs;
            Owner = owner;
            Title = "Sign in to Nexus Mods";
            Width = 900; Height = 680;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Content = _webView;
            Closed += (_, _) => { if (!_tcs.Task.IsCompleted) _tcs.SetResult(null); };
            Loaded += async (_, _) => await InitWebViewAsync();
        }

        private async Task InitWebViewAsync()
        {
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: WebViewDataPath);
            await _webView.EnsureCoreWebView2Async(env);
            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _webView.CoreWebView2.Navigate(NEXUS_LOGIN);
        }

        private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            var url = _webView.Source?.ToString() ?? "";

            await AutoAcceptCookiesAsync();

            if (_fetchStarted) return;

            if (url.Contains("users.nexusmods.com"))
            {
                _cameFromUsers = true;

                // 로그인 완료 페이지 감지 → 자동으로 넥서스 홈으로 이동
                if (url.Contains("/account/security") || url.Contains("/account"))
                {
                    Logger.Info("NexusWebLogin: login complete, redirecting to home");
                    _webView.CoreWebView2.Navigate(NEXUS_HOME);
                }
                return;
            }

            bool isHome = url.StartsWith(LOGGED_IN_URL) && _cameFromUsers;
            if (!isHome) return;

            _fetchStarted = true;
            Title = "Fetching download history...";
            Logger.Info("NexusWebLogin: login confirmed, fetching history");

            try
            {
                var history = await FetchHistoryAsync();
                Logger.Info($"NexusWebLogin: fetched {history.Count} BG3 entries");
                _tcs.SetResult(history);
            }
            catch (Exception ex)
            {
                Logger.Warn($"NexusWebLogin: fetch failed ({ex.Message}), returning empty");
                _tcs.SetResult(new List<NexusDownloadEntry>());
            }
            finally { Close(); }
        }

        private async Task<List<NexusDownloadEntry>> FetchHistoryAsync()
        {
            // 전역 변수에 결과 저장 후 polling — ExecuteScriptAsync의 Promise 미지원 우회
            await _webView.CoreWebView2.ExecuteScriptAsync(
                "window.__bg3Result = null;" +
                "window.__bg3Error  = null;" +
                "fetch('" + HISTORY_URL + "'," +
                "  { credentials: 'include', headers: { 'X-Requested-With': 'XMLHttpRequest' } })" +
                ".then(r => r.json())" +
                ".then(d => { window.__bg3Result = JSON.stringify(d); })" +
                ".catch(e => { window.__bg3Error = e.toString(); });");

            // 최대 15초 대기
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(500);

                var err = await _webView.CoreWebView2.ExecuteScriptAsync("window.__bg3Error");
                if (err != "null")
                {
                    Logger.Error("NexusWebLogin: JS error — " + err);
                    return new();
                }

                var raw = await _webView.CoreWebView2.ExecuteScriptAsync("window.__bg3Result");
                if (raw == "null") continue;

                // raw는 JSON 인코딩된 문자열 → 언이스케이프
                var json = System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? raw;
                Logger.Info("NexusWebLogin: history JSON received, parsing...");
                return ParseHistory(json);
            }

            Logger.Warn("NexusWebLogin: timeout waiting for history response");
            return new();
        }

        private async Task AutoAcceptCookiesAsync()
        {
            try
            {
                await _webView.CoreWebView2.ExecuteScriptAsync(
                    "(function(){" +
                    "var b=document.getElementById('CybotCookiebotDialogBodyButtonAccept');" +
                    "if(b)b.click();})()");
            }
            catch { }
        }

        private static List<NexusDownloadEntry> ParseHistory(string json)
        {
            var result = new List<NexusDownloadEntry>();
            try
            {
                var data = JObject.Parse(json)["data"] as JArray;
                if (data == null) return result;
                foreach (var row in data)
                {
                    var arr = row as JArray;
                    if (arr == null || arr.Count < 10) continue;
                    var domain = arr[9].Value<string>() ?? "";
                    if (!domain.Equals("baldursgate3", StringComparison.OrdinalIgnoreCase)) continue;
                    var modId = arr[8].Value<int>();
                    var name  = arr[1].Value<string>() ?? "";
                    if (modId > 0) result.Add(new NexusDownloadEntry(modId, name));
                }
            }
            catch (Exception ex) { Logger.Warn("ParseHistory: " + ex.Message); }
            return result;
        }
    }
}

public record NexusDownloadEntry(int ModId, string Name);
