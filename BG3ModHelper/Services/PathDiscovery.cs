using System.IO;

namespace BG3ModHelper.Services;

/// <summary>
/// Helpers for discovering BG3-related paths on the local machine.
/// </summary>
public static class PathDiscovery
{
    /// <summary>
    /// Returns the default BG3 mods folder path.
    /// = %LOCALAPPDATA%\Larian Studios\Baldur's Gate 3\Mods
    /// </summary>
    public static string GetDefaultModsFolder()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, Constants.BG3_MODS_FOLDER_RELATIVE);
    }

    /// <summary>Returns true if the given mods folder path exists on disk.</summary>
    public static bool ModsFolderExists(string path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);

    /// <summary>Returns true if the given folder contains BG3ModManager.exe.</summary>
    public static bool IsValidBG3MMFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return File.Exists(Path.Combine(path, Constants.BG3MM_EXE_NAME));
    }

    /// <summary>
    /// Counts .pak files in the mods folder without parsing them.
    /// Used for a quick "installed mods" display count.
    /// </summary>
    public static int CountPakFiles(string modsFolder)
    {
        if (!ModsFolderExists(modsFolder)) return 0;
        try
        {
            return Directory.GetFiles(modsFolder, "*.pak").Length;
        }
        catch
        {
            return 0;
        }
    }
}
