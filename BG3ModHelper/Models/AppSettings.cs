namespace BG3ModHelper.Models;

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

    // === Folder Watcher ===

    /// <summary>Whether download folder auto-install watching is enabled.</summary>
    public bool   FolderWatchEnabled      { get; set; } = false;

    /// <summary>Path to watch. Empty = OS default Downloads folder.</summary>
    public string WatchedDownloadFolder   { get; set; } = "";

    /// <summary>Delete source archive/pak after a successful install.</summary>
    public bool   DeleteSourceAfterInstall { get; set; } = false;

    // === Compact Mode ===

    /// <summary>Compact window size in pixels (square). Default 160.</summary>
    public int    CompactSize    { get; set; } = 160;

    /// <summary>Compact window background opacity. 0.3~1.0, default 0.85.</summary>
    public double CompactOpacity { get; set; } = 0.85;

    /// <summary>Last compact window position.</summary>
    public double CompactX    { get; set; } = 100;
    public double CompactY    { get; set; } = 100;

    /// <summary>Whether the app was last closed in Compact mode.</summary>
    public bool LastModeIsCompact { get; set; } = false;

    // === WebView ===

    /// <summary>Zoom factor for the slide WebView panel. 1.0 = 100%.</summary>
    public double WebViewZoom { get; set; } = 1.0;

    // === nxm:// Protocol Handler ===

    /// <summary>Whether the app is currently registered as the nxm:// handler.</summary>
    public bool   NxmHandlerEnabled  { get; set; } = false;

    /// <summary>
    /// Backup of the nxm:// handler command that existed before we registered.
    /// Used to forward non-BG3 nxm URLs to the original handler (e.g. Vortex).
    /// Format: "C:\path\to\app.exe" "%1"
    /// </summary>
    public string NxmPreviousHandler { get; set; } = "";

    /// <summary>
    /// All nxm:// handler commands ever seen/backed up.
    /// Used to populate the Secondary Handler dropdown in Settings.
    /// </summary>
    public List<string> NxmKnownHandlers { get; set; } = [];
}
