namespace BG3MM_UpdateHelper.Models;

public class AppSettings
{
    public string   BG3MMFolderPath   { get; set; } = "";
    public string   ModsFolderPath    { get; set; } = "";
    public string   NexusAPIKey       { get; set; } = "";
    public string   ModioAPIKey       { get; set; } = "";
    public bool     NexusIsPremium    { get; set; } = false;
    public DateTime LastPremiumCheck  { get; set; } = DateTime.MinValue;
    public int      CacheExpiryHours  { get; set; } = 6;

    /// <summary>
    /// Back up existing .pak as .pak.bak before installing an update.
    /// Disabled by default — enabling doubles disk usage for updated mods.
    /// </summary>
    public bool BackupBeforeUpdate { get; set; } = false;

    /// <summary>Last time Nexus download history was synced (24h throttle).</summary>
    public DateTime LastNexusHistorySync  { get; set; } = DateTime.MinValue;
    public int      LastNexusHistoryCount { get; set; } = 0;
}
