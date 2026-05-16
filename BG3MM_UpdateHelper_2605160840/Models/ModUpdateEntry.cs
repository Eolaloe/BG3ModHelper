namespace BG3MM_UpdateHelper.Models;

public class ModUpdateEntry
{
    public string UUID           { get; set; } = "";
    public ulong  PublishHandle  { get; set; }
    public int?   NexusModId     { get; set; }  // Nexus mod ID (있으면 업데이트 체크 가능)
    public string ModName        { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string NewVersion     { get; set; } = "";

    public UpdateSource        DefaultSource    { get; set; }
    public List<UpdateSource>  AvailableSources { get; set; } = new();
    public UpdateSource?       PreferredSource  { get; set; }

    public string NexusUrl  { get; set; } = "";
    public string ModioUrl   { get; set; } = "";
    public string PakFilePath { get; set; } = "";  // existing .pak path (for backup)

    public bool   CanAutoDownload { get; set; }
    public string Changelog       { get; set; } = "";

    public UpdateStatus Status { get; set; } = UpdateStatus.Pending;
}

public enum UpdateStatus
{
    Pending,
    Downloading,
    Installing,
    Done,
    Failed,
    Skipped
}
