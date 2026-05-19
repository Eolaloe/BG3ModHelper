using System.IO;
using BG3ModHelper.Models;
using Newtonsoft.Json;

namespace BG3ModHelper.Services;

/// <summary>
/// Loads and saves settings.json.
/// Location: %LOCALAPPDATA%\BG3ModHelper\settings.json
/// </summary>
public static class SettingsStore
{
    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Constants.APP_DATA_FOLDER);

    private static readonly string SettingsFilePath = Path.Combine(DataFolder, "settings.json");

    /// <summary>Returns true if settings.json exists (i.e. not the first run).</summary>
    public static bool Exists() => File.Exists(SettingsFilePath);

    /// <summary>Loads settings from disk. Returns defaults if the file does not exist.</summary>
    public static AppSettings Load()
    {
        if (!File.Exists(SettingsFilePath))
            return new AppSettings();

        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            return JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to load settings: {ex.Message}");
            return new AppSettings();
        }
    }

    /// <summary>Saves settings to disk.</summary>
    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DataFolder);
        var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
        File.WriteAllText(SettingsFilePath, json);
    }

    /// <summary>
    /// Returns the helper data folder path, creating it if necessary.
    /// Used by other services to store cache files and logs.
    /// </summary>
    public static string GetDataFolder()
    {
        Directory.CreateDirectory(DataFolder);
        return DataFolder;
    }
}
