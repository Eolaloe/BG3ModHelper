namespace BG3MM_UpdateHelper.Models;

public class ModUpdateEntry
{
    public string MetaUuid            { get; set; } = "";
    public ulong  ModioPublishHandle  { get; set; }
    public int?   NexusModId          { get; set; }
    public string UpdateModName       { get; set; } = "";
    public string UpdateCurrentVersion { get; set; } = "";
    public string UpdateNewVersion    { get; set; } = "";  // higher of the two (display fallback)

    // Per-source versions — used when switching active source
    public string ModioFileVersion { get; set; } = "";
    public string NexusFileVersion { get; set; } = "";

    // Recorded after download for accurate update detection (spec §4.11)
    public long NexusFileId { get; set; }

    public UpdateSource        DefaultSource    { get; set; }
    public List<UpdateSource>  AvailableSources { get; set; } = new();
    public UpdateSource?       PreferredSource  { get; set; }

    public string NexusModPageUrl { get; set; } = "";
    public string ModioProfileUrl { get; set; } = "";
    public string PakFilePath     { get; set; } = "";

    public bool   CanAutoDownload { get; set; }
    public string Changelog       { get; set; } = "";

    public UpdateStatus Status { get; set; } = UpdateStatus.Pending;
}

public enum UpdateStatus
{
    Pending,
    Downloading,
    Applying,
    Updated,
    Failed,
    Retry,
    Skipped
}
