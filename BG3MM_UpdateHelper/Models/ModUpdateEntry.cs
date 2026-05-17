namespace BG3MM_UpdateHelper.Models;

public class ModUpdateEntry
{
    public string UUID           { get; set; } = "";
    public ulong  PublishHandle  { get; set; }
    public int?   NexusModId     { get; set; }
    public string ModName        { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string NewVersion     { get; set; } = "";  // higher of the two (display fallback)

    // Per-source versions — used when switching active source
    public string ModioNewVersion { get; set; } = "";
    public string NexusNewVersion { get; set; } = "";

    // Recorded after download for accurate update detection (spec §4.11)
    public long NexusFileId { get; set; }

    public UpdateSource        DefaultSource    { get; set; }
    public List<UpdateSource>  AvailableSources { get; set; } = new();
    public UpdateSource?       PreferredSource  { get; set; }

    public string NexusUrl    { get; set; } = "";
    public string ModioUrl    { get; set; } = "";
    public string PakFilePath { get; set; } = "";

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
