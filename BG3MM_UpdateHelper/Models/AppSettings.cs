namespace BG3MM_UpdateHelper.Models;

public class AppSettings
{
    public string   BG3MMFolderPath    { get; set; } = "";
    public string   ModsFolderPath     { get; set; } = "";
    public string   NexusAPIKey        { get; set; } = "";
    public string   ModioAPIKey        { get; set; } = "";
    public bool     NexusIsPremium     { get; set; } = false;
    public DateTime LastPremiumCheck   { get; set; } = DateTime.MinValue;
    public bool     BackupBeforeUpdate { get; set; } = false;

    /// <summary>Last update check time — persisted so it survives app restarts.</summary>
    public DateTime? LastCheck { get; set; } = null;

    // ── Folder Watcher ────────────────────────────────────────────────────

    /// <summary>Whether download folder auto-install watching is enabled.</summary>
    public bool   FolderWatchEnabled      { get; set; } = false;

    /// <summary>Path to watch. Empty = OS default Downloads folder.</summary>
    public string WatchedDownloadFolder   { get; set; } = "";
}
