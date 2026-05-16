namespace BG3MM_UpdateHelper.Models;

public class ModUpdateEntry
{
    public string UUID           { get; set; } = "";
    public ulong  PublishHandle  { get; set; }
    public int?   NexusModId     { get; set; }
    public string ModName        { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string NewVersion     { get; set; } = "";  // 더 높은 쪽 (표시 fallback)

    // 소스별 버전 — 소스 전환 시 버전 표기 변경에 사용
    public string ModioNewVersion { get; set; } = "";
    public string NexusNewVersion { get; set; } = "";

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
    Installing,
    Done,
    Failed,
    Skipped
}
