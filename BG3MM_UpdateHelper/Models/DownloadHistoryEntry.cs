namespace BG3MM_UpdateHelper.Models;

/// <summary>
/// A single download history record.
/// Stored in %LOCALAPPDATA%\BG3MM_UpdateHelper\download_history.json
/// </summary>
public class DownloadHistoryEntry
{
    /// <summary>UTC timestamp of the download attempt.</summary>
    public DateTime HistoryDownloadedAt { get; set; }

    /// <summary>Display name of the mod (MetaModuleName from pak).</summary>
    public string   HistoryModName      { get; set; } = "";

    /// <summary>Platform mod name at download time (NexusModName or ModioModName). Empty for older records.</summary>
    public string   HistoryPlatformModName { get; set; } = "";

    /// <summary>Version before update. "Not installed" if newly installed.</summary>
    public string   HistoryFromVersion  { get; set; } = "";

    /// <summary>Version after update.</summary>
    public string   HistoryToVersion    { get; set; } = "";

    /// <summary>Download source: "Nexus", "ModIO", "Others", "Nxm" (future).</summary>
    public string   HistorySource       { get; set; } = "";

    /// <summary>
    /// URL to open when the mod name is clicked.
    /// Null for "Others" (no known page).
    /// </summary>
    public string?  HistoryPageUrl      { get; set; }

    /// <summary>True if download and installation both succeeded.</summary>
    public bool     HistorySuccess      { get; set; }
}
