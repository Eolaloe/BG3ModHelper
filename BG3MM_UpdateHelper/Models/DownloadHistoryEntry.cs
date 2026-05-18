namespace BG3MM_UpdateHelper.Models;

/// <summary>
/// A single download history record.
/// Stored in %LOCALAPPDATA%\BG3MM_UpdateHelper\download_history.json
/// </summary>
public class DownloadHistoryEntry
{
    /// <summary>UTC timestamp of the download attempt.</summary>
    public DateTime DownloadedAt  { get; set; }

    /// <summary>Display name of the mod.</summary>
    public string   ModName       { get; set; } = "";

    /// <summary>Version before update. "Not installed" if newly installed.</summary>
    public string   FromVersion   { get; set; } = "";

    /// <summary>Version after update.</summary>
    public string   ToVersion     { get; set; } = "";

    /// <summary>Download source: "Nexus", "ModIO", "Others", "Nxm" (future).</summary>
    public string   Source        { get; set; } = "";

    /// <summary>
    /// URL to open when the mod name is clicked.
    /// Null for "Others" (no known page).
    /// </summary>
    public string?  PageUrl       { get; set; }

    /// <summary>True if download and installation both succeeded.</summary>
    public bool     Success       { get; set; }
}
