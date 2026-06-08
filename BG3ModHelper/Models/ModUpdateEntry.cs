namespace BG3ModHelper.Models;

public class ModUpdateEntry
{
    public string MetaUuid            { get; set; } = "";
    public ulong  ModioPublishHandle  { get; set; }
    public int?   NexusModId          { get; set; }
    public string UpdateModName       { get; set; } = "";  // MetaModuleName (local pak)
    public string NexusModName        { get; set; } = "";  // from Nexus DB
    public string ModioModName        { get; set; } = "";  // from mod.io API
    public string UpdateCurrentVersion { get; set; } = "";
    public string UpdateNewVersion    { get; set; } = "";  // higher of the two (display fallback)

    // Per-source versions — used when switching active source
    public string ModioFileVersion { get; set; } = "";
    public string NexusFileVersion { get; set; } = "";

    // Recorded after download for accurate update detection (spec §4.11)
    public long   NexusFileId   { get; set; }
    /// <summary>Nexus file display name for the specific variant, e.g. "Better Map 0.85 scale".</summary>
    public string NexusFileName { get; set; } = "";

    public UpdateSource        DefaultSource    { get; set; }
    public List<UpdateSource>  AvailableSources { get; set; } = new();
    public UpdateSource?       PreferredSource  { get; set; }

    public string NexusModPageUrl { get; set; } = "";
    public string ModioProfileUrl { get; set; } = "";
    public string PakFilePath     { get; set; } = "";

    public bool   CanAutoDownload     { get; set; }
    public bool   RequiresManualCheck { get; set; }

    /// <summary>
    /// True when version comparison is unreliable (no stored fileId + version scheme mismatch).
    /// Entry appears in the "Sync Recommended" section to prompt a one-time re-download.
    /// Once downloaded through the app, fileId is stored and future tracking is accurate.
    /// </summary>
    public bool   IsSyncRequired     { get; set; }
    public string Changelog        { get; set; } = "";  // Nexus changelog
    public string ModioChangelog   { get; set; } = "";  // mod.io changelog

    public UpdateStatus Status   { get; set; } = UpdateStatus.Pending;

    /// <summary>True when the mod is in the active load order (modsettings.lsx).</summary>
    public bool IsActive { get; set; } = true;
}

public enum UpdateStatus
{
    Pending,
    Downloading,
    Applying,
    Updated,
    Failed,
    Retry,
    Skipped,
    /// <summary>
    /// Nexus returned 403 on download_link — manager downloads disabled for this file.
    /// Entry will be routed to the WebView slide queue for manual download.
    /// </summary>
    ManualRequired
}
