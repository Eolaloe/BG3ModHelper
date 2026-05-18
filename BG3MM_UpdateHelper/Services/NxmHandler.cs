using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace BG3MM_UpdateHelper.Services;

/// <summary>
/// Manages Windows registry registration for the nxm:// protocol.
/// Writes to HKCU (user scope) — no admin rights required.
/// </summary>
public static partial class NxmHandler
{
    private const string SubKey = @"Software\Classes\nxm";

    // Matches the leading quoted exe path:  "C:\path\to\app.exe" "%1"  →  C:\path\to\app.exe
    [GeneratedRegex("^\"([^\"]+)\"")]
    private static partial Regex QuotedExePattern();

    /// <summary>
    /// Reads the current registered handler command, or null if not registered.
    /// Format example: "C:\path\to\Vortex.exe" "%1"
    /// </summary>
    public static string? ReadCurrentCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SubKey + @"\shell\open\command");
            return key?.GetValue("") as string;
        }
        catch (Exception ex)
        {
            Logger.Warn($"NxmHandler.ReadCurrentCommand: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Registers this app as the nxm:// handler in HKCU.
    /// Overwrites any existing handler — caller is responsible for backing it up first.
    /// </summary>
    public static bool Register()
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                Logger.Warn("NxmHandler.Register: failed to resolve exe path");
                return false;
            }

            var selfCmd = $"\"{exePath}\" \"%1\"";

            using (var key = Registry.CurrentUser.CreateSubKey(SubKey))
            {
                key.SetValue("",             "URL:Nexus Mods Protocol");
                key.SetValue("URL Protocol", "");

                using var cmd = key.CreateSubKey(@"shell\open\command");
                cmd.SetValue("", selfCmd);
            }

            Logger.Info($"NxmHandler.Register: registered self ({exePath})");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"NxmHandler.Register: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Removes the nxm:// registration entirely. Returns true if removed or already absent.
    /// </summary>
    public static bool Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(SubKey, throwOnMissingSubKey: false);
            Logger.Info("NxmHandler.Unregister: removed");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"NxmHandler.Unregister: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Checks if this app is currently the registered handler.
    /// </summary>
    public static bool IsRegisteredToSelf()
    {
        var current = ReadCurrentCommand();
        if (string.IsNullOrEmpty(current)) return false;

        var registered = ExtractExePath(current);
        if (registered is null) return false;

        var self = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(self)) return false;

        return string.Equals(
            Path.GetFullPath(registered),
            Path.GetFullPath(self),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Registers an arbitrary command string as the nxm:// handler.
    /// Used when the user selects a non-self app as primary from the dropdown.
    /// </summary>
    public static bool RegisterCommand(string command)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SubKey);
            key.SetValue("",             "URL:Nexus Mods Protocol");
            key.SetValue("URL Protocol", "");
            using var cmd = key.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", command);
            Logger.Info($"NxmHandler.RegisterCommand: registered external handler");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"NxmHandler.RegisterCommand: {ex.Message}");
            return false;
        }
    }

    /// <summary>Returns the command string that would register this app as the handler.</summary>
    public static string GetSelfCommand()
    {
        var exe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
        return $"\"{exe}\" \"%1\"";
    }
    public static string? ExtractExePath(string command)
    {
        if (string.IsNullOrEmpty(command)) return null;
        var match = QuotedExePattern().Match(command);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Enables nxm handling: backs up the current registered command (if any
    /// and not ours) into settings, then registers this app as the handler.
    /// Returns true on success.
    /// </summary>
    public static bool EnableHandler(Models.AppSettings settings)
    {
        var current = ReadCurrentCommand();
        if (!string.IsNullOrEmpty(current) && !IsRegisteredToSelf())
        {
            settings.NxmPreviousHandler = current;
            Logger.Info($"NxmHandler.EnableHandler: backed up previous handler ({current})");

            // Accumulate in known handlers for dropdown
            if (!settings.NxmKnownHandlers.Contains(current))
                settings.NxmKnownHandlers.Add(current);
        }

        if (!Register()) return false;

        settings.NxmHandlerEnabled = true;
        SettingsStore.Save(settings);
        return true;
    }

    /// <summary>
    /// Disables nxm handling: removes the registry key but keeps the backup
    /// (so the user can re-enable later and restore forwarding to the same
    /// backup manager).
    /// </summary>
    public static bool DisableHandler(Models.AppSettings settings)
    {
        // Restore secondary as primary (Vortex/MO2 takes over cleanly)
        if (!string.IsNullOrEmpty(settings.NxmPreviousHandler))
        {
            var exe = ExtractExePath(settings.NxmPreviousHandler);
            if (!string.IsNullOrEmpty(exe) && System.IO.File.Exists(exe))
                RegisterCommand(settings.NxmPreviousHandler);
            else
                Unregister();
        }
        else
        {
            Unregister();
        }

        settings.NxmHandlerEnabled  = false;
        settings.NxmPreviousHandler = "";   // Secondary cleared — next ON will re-detect
        SettingsStore.Save(settings);
        return true;
    }
}
