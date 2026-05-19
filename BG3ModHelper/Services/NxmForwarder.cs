using System.Diagnostics;
using System.IO;

namespace BG3ModHelper.Services;

/// <summary>
/// Forwards non-BG3 nxm:// URLs to the backed-up handler (e.g. Vortex).
/// </summary>
public static class NxmForwarder
{
    /// <summary>
    /// Forwards a raw nxm URL to the backed-up handler.
    /// Returns true on success, false if backup is missing or failed.
    /// </summary>
    public static bool Forward(string rawNxmUrl, string backupCommand)
    {
        if (string.IsNullOrEmpty(backupCommand))
        {
            Logger.Warn($"NxmForwarder: no backup handler configured for {rawNxmUrl}");
            return false;
        }

        var exe = NxmHandler.ExtractExePath(backupCommand);
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            Logger.Warn($"NxmForwarder: backup exe not found ({exe ?? "(null)"})");
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = exe,
                Arguments       = $"\"{rawNxmUrl}\"",
                UseShellExecute = false,
            });
            Logger.Info($"NxmForwarder: forwarded to {exe}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"NxmForwarder: launch failed — {ex.Message}");
            return false;
        }
    }
}
