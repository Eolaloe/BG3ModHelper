using System.Net.Http;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json.Linq;

namespace BG3MM_UpdateHelper.Services;

/// <summary>
/// Opens an in-app browser (WebView2) for the user to log in to Nexus Mods,
/// then fetches their BG3 download history using the session cookie.
/// </summary>
public static class NexusWebLogin
{
    private const string NEXUS_HOME    = "https://www.nexusmods.com";
    private const string NEXUS_LOGIN   = "https://users.nexusmods.com/";
    private const string HISTORY_URL   = "https://www.nexusmods.com/Core/Libs/Common/Managers/Mods?GetDownloadHistory";
    private const string LOGGED_IN_URL = "https://www.nexusmods.com/";
    private const int    BG3_GAME_ID   = 3474;

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Shows a WebView2 login window. Returns BG3 download history on success,
    /// or null if the user cancelled.
    /// </summary>
    public static async Task<List<NexusDownloadEntry>?> LoginAndFetchHistoryAsync(
        Window owner)
    {
        var tcs = new TaskCompletionSource<List<NexusDownloadEntry>?>();

        var window = new NexusLoginWindow(owner, tcs);
        window.ShowDialog();

        return await tcs.Task;
    }

    // ── Login Window ──────────────────────────────────────────────────────

    private class NexusLoginWindow : Window
    {
        private readonly WebView2 _webView = new();
        private readonly TaskCompletionSource<List<NexusDownloadEntry>?> _tcs;
        private bool _fetchStarted;

        public NexusLoginWindow(
            Window owner,
            TaskCompletionSource<List<NexusDownloadEntry>?> tcs)
        {
            _tcs = tcs;

            Owner                 = owner;
            Title                 = "Sign in to Nexus Mods";
            Width                 = 900;
            Height                = 680;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Content               = _webView;

            Closed += (_, _) =>
            {
                if (!_tcs.Task.IsCompleted)
                    _tcs.SetResult(null); // cancelled
            };

            Loaded += async (_, _) => await InitWebViewAsync();
        }

        private async Task InitWebViewAsync()
        {
            await _webView.EnsureCoreWebView2Async();
            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _webView.CoreWebView2.Navigate(NEXUS_LOGIN);
        }

        private async void OnNavigationCompleted(
            object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            var url = _webView.Source?.ToString() ?? "";

            // 1. 쿠키 동의 팝업 자동 수락 (Cookiebot)
            await AutoAcceptCookiesAsync();

            if (_fetchStarted) return;

            // 2. users.nexusmods.com에서 로그인 완료 감지
            //    → 로그인 성공 시 앱이 직접 nexusmods.com 홈으로 이동
            if (url.Contains("users.nexusmods.com"))
            {
                var loginCookies = await _webView.CoreWebView2.CookieManager
                    .GetCookiesAsync("https://users.nexusmods.com");
                bool hasSession  = loginCookies.Any(c =>
                    (c.Name.Equals("sid", StringComparison.OrdinalIgnoreCase) ||
                     c.Name.Equals("sid_develop", StringComparison.OrdinalIgnoreCase) ||
                     c.Name.Contains("session", StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrEmpty(c.Value));

                if (hasSession)
                {
                    Logger.Info("NexusWebLogin: session detected, navigating to home");
                    _webView.CoreWebView2.Navigate(NEXUS_HOME);
                }
                return;
            }

            // 3. nexusmods.com 홈 도달 → 히스토리 수집
            bool isHome = url.StartsWith(LOGGED_IN_URL) &&
                          !url.Contains("users.nexusmods.com");
            if (!isHome) return;

            // member_id 쿠키로 실제 로그인 여부 확인
            var cookies   = await _webView.CoreWebView2.CookieManager
                .GetCookiesAsync(NEXUS_HOME);
            bool loggedIn = cookies.Any(c =>
                c.Name.Equals("member_id", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(c.Value));

            if (!loggedIn) return;
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
                Logger.Error($"NexusWebLogin: history fetch failed — {ex.Message}");
                _tcs.SetResult(null);
            }
            finally
            {
                Close();
            }
        }

        /// <summary>Cookiebot 팝업의 Allow all 버튼을 JavaScript로 자동 클릭.</summary>
        private async Task AutoAcceptCookiesAsync()
        {
            try
            {
                await _webView.CoreWebView2.ExecuteScriptAsync("""
                    (function() {
                        // Cookiebot
                        var btn = document.getElementById('CybotCookiebotDialogBodyButtonAccept');
                        if (btn) { btn.click(); return; }
                        // 일반 패턴
                        var btns = document.querySelectorAll(
                            'button[id*="accept"], button[class*="accept"], ' +
                            'button[id*="Allow"], button[class*="allow-all"]');
                        if (btns.length > 0) btns[0].click();
                    })();
                """);
            }
            catch { /* 팝업 없으면 무시 */ }
        }

        private async Task<List<NexusDownloadEntry>> FetchHistoryAsync()
        {
            // WebView2의 쿠키를 사용해서 GetDownloadHistory API 호출
            var cookieList = await _webView.CoreWebView2.CookieManager
                .GetCookiesAsync(NEXUS_HOME);

            var cookieHeader = string.Join("; ",
                cookieList.Select(c => $"{c.Name}={c.Value}"));

            using var http    = new HttpClient();
            var request       = new HttpRequestMessage(HttpMethod.Get, HISTORY_URL);
            request.Headers.Add("Cookie",           cookieHeader);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Headers.Add("Referer",          "https://www.nexusmods.com/users/myaccount?tab=download+history");
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            return ParseHistory(json);
        }

        private static List<NexusDownloadEntry> ParseHistory(string json)
        {
            var result = new List<NexusDownloadEntry>();
            var data   = JObject.Parse(json)["data"] as JArray;
            if (data == null) return result;

            foreach (var row in data)
            {
                // 컬럼: [0]thumbnail [1]name [2]lastUpload [3]author [4]category
                //       [5]downloadTs [6]? [7]gameId [8]modId [9]gameDomain ...
                var arr = row as JArray;
                if (arr == null || arr.Count < 10) continue;

                var gameDomain = arr[9].Value<string>() ?? "";
                if (!gameDomain.Equals("baldursgate3", StringComparison.OrdinalIgnoreCase))
                    continue;

                var modId = arr[8].Value<int>();
                var name  = arr[1].Value<string>() ?? "";

                if (modId > 0)
                    result.Add(new NexusDownloadEntry(modId, name));
            }

            return result;
        }
    }
}

/// <summary>Single entry from Nexus download history.</summary>
public record NexusDownloadEntry(int ModId, string Name);
