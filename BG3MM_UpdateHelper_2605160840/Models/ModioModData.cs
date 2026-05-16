namespace BG3MM_UpdateHelper.Models;

/// <summary>Mod metadata fetched from the mod.io API.</summary>
public class ModioModData
{
    /// <summary>mod.io global mod ID — matches meta.lsx PublishHandle.</summary>
    public ulong   ModId      { get; set; }
    public string  Name       { get; set; } = "";
    public string  Summary    { get; set; } = "";
    public string  ProfileUrl { get; set; } = "";
    public string  LatestVersion { get; set; } = "";
    public DateTime UpdatedAt { get; set; }

    /// <summary>Latest modfile ID — passed to GetLatestFileAsync for the download URL.</summary>
    public long LatestFileId { get; set; }
}

/// <summary>A single modfile entry containing the download URL.</summary>
public class ModioModFile
{
    public long   Id          { get; set; }
    public string Version     { get; set; } = "";
    public string FileName    { get; set; } = "";

    /// <summary>Temporary download URL. Expires at DateExpires.</summary>
    public string BinaryUrl   { get; set; } = "";

    /// <summary>Unix timestamp when BinaryUrl expires.</summary>
    public long   DateExpires { get; set; }

    public bool IsExpired =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= DateExpires;
}
