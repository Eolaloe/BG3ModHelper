using System.IO;
using Microsoft.Web.WebView2.Core;

namespace BG3MM_UpdateHelper.Services;

public static class WebViewHelper
{
    public static string UserDataFolder { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BG3MM_UpdateHelper",
            "WebView2");

    private static readonly string FlagFile =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BG3MM_UpdateHelper",
            "nexus_session.flag");

    private static WeakReference<CoreWebView2>? _activeCore;
    private static bool _loginState;

    static WebViewHelper()
    {
        _loginState = File.Exists(FlagFile);
    }

    public static event Action<bool>? LoginStateChanged;

    public static bool IsLoggedIn() => _loginState;

    public static void RegisterWebView(CoreWebView2 core) =>
        _activeCore = new WeakReference<CoreWebView2>(core);

    public static Task RegisterWebViewAsync(CoreWebView2 core)
    {
        _activeCore = new WeakReference<CoreWebView2>(core);
        return Task.CompletedTask;
    }

    public static void UnregisterWebView() => _activeCore = null;

    public static void SetLoggedIn(bool value)
    {
        _loginState = value;

        if (value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FlagFile)!);
            File.WriteAllText(FlagFile, "");
        }
        else
        {
            if (File.Exists(FlagFile)) File.Delete(FlagFile);
        }

        LoginStateChanged?.Invoke(value);
    }

    public static async Task LogoutAsync()
    {
        _loginState = false;
        if (File.Exists(FlagFile)) File.Delete(FlagFile);

        if (_activeCore != null && _activeCore.TryGetTarget(out var core))
        {
            try
            {
                // Nexus 서버 세션 실제 만료
                var tcs = new TaskCompletionSource<bool>();
                EventHandler<CoreWebView2NavigationCompletedEventArgs>? handler = null;
                handler = (_, _) =>
                {
                    core.NavigationCompleted -= handler;
                    tcs.TrySetResult(true);
                };
                core.NavigationCompleted += handler;
                core.Navigate("https://users.nexusmods.com/auth/sign_out");
                await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8));
            }
            catch { }

            try { await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllSite); }
            catch { }
        }
        else
        {
            if (Directory.Exists(UserDataFolder))
                try { Directory.Delete(UserDataFolder, recursive: true); } catch { }
        }

        LoginStateChanged?.Invoke(false);
    }
}
