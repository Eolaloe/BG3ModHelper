namespace BG3ModHelper.Models;

/// <summary>
/// A single download history record.
/// Stored in %LOCALAPPDATA%\BG3ModHelper\download_history.json
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

    /// <summary>
    /// Non-null when this entry is part of a group download (multiple paks, one zip).
    /// All entries in the same group share the same GroupId.
    /// </summary>
    public string?  HistoryGroupId      { get; set; }

    /// <summary>Actual .pak filename installed (not the mod display name).</summary>
    public string?  HistoryPakFileName         { get; set; }

    /// <summary>
    /// Non-null when an existing pak with the same UUID+ModuleName but a different
    /// filename was removed before installing this one (cross-platform rename case).
    /// Value is the old pak filename that was deleted.
    /// </summary>
    public string?  HistoryReplacedPakFileName { get; set; }
}
