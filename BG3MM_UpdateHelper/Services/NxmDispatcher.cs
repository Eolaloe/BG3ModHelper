using System.Windows;
using BG3MM_UpdateHelper.Models;

namespace BG3MM_UpdateHelper.Services;

/// <summary>
/// Entry point for incoming nxm:// URLs.
/// Validates, then either enqueues (BG3) or forwards to backup handler (other games).
/// </summary>
public static class NxmDispatcher
{
    /// <summary>
    /// Handles a raw nxm:// URL string received from OS / CLI / IPC.
    /// </summary>
    public static void Dispatch(string rawUrl)
    {
        var (result, url) = NxmValidator.TryParse(rawUrl);

        switch (result)
        {
            case NxmValidator.ValidationResult.Valid:
                if (url!.IsBG3)
                {
                    NxmDownloadQueue.Instance.Enqueue(url, rawUrl);
                }
                else
                {
                    HandleNonBG3(url, rawUrl);
                }
                break;

            case NxmValidator.ValidationResult.Expired:
                Logger.Warn($"NxmDispatcher: token expired for {rawUrl}");
                ShowTokenExpiredDialog(url);
                break;

            default:
                Logger.Warn($"NxmDispatcher: rejected URL ({result}): {rawUrl}");
                break;
        }
    }

    private static void HandleNonBG3(NxmUrl url, string rawUrl)
    {
        var settings = SettingsStore.Load();

        if (string.IsNullOrEmpty(settings.NxmPreviousHandler))
        {
            Logger.Warn($"NxmDispatcher: non-BG3 nxm ({url.Game}) but no backup handler set");
            ShowNoBackupDialog(url);
            return;
        }

        var ok = NxmForwarder.Forward(rawUrl, settings.NxmPreviousHandler);
        if (!ok)
            ShowNoBackupDialog(url);
    }

    private static void ShowTokenExpiredDialog(NxmUrl? url)
    {
        var modInfo = url is null ? "" : $"\n\nMod: {url.Game}/mods/{url.NexusModId}/files/{url.NexusFileId}";
        Application.Current?.Dispatcher.InvokeAsync(() =>
            MessageBox.Show(
                "The Nexus download token has expired.\n\n" +
                "Tokens are short-lived. Please return to the mod page and " +
                "click \"Download with Manager\" again." + modInfo,
                "Token Expired",
                MessageBoxButton.OK,
                MessageBoxImage.Warning));
    }

    private static void ShowNoBackupDialog(NxmUrl url)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
            MessageBox.Show(
                $"This nxm link is for {url.Game}, which is not Baldur's Gate 3.\n\n" +
                "No backup mod manager (e.g. Vortex) is configured to handle " +
                "downloads for other games.",
                "No Handler for This Game",
                MessageBoxButton.OK,
                MessageBoxImage.Information));
    }
}
