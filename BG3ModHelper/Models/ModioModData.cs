namespace BG3ModHelper.Models;

/// <summary>Mod metadata fetched from the mod.io API.</summary>
public class ModioModData
{
    /// <summary>mod.io global mod ID — matches meta.lsx PublishHandle.</summary>
    public ulong   ModioModId        { get; set; }
    public string  ModioModName      { get; set; } = "";
    public string  ModioSummary      { get; set; } = "";
    public string  ModioProfileUrl   { get; set; } = "";
    public string  ModioFileVersion  { get; set; } = "";
    public DateTime ModioDateUpdated { get; set; }

    /// <summary>Latest modfile ID — passed to GetLatestFileAsync for the download URL.</summary>
    public long ModioFileId { get; set; }
}

/// <summary>A single modfile entry containing the download URL.</summary>
public class ModioModFile
{
    public long   ModioFileId   { get; set; }
    public string ModioFileVersion { get; set; } = "";
    public string ModioFileName { get; set; } = "";

    /// <summary>Temporary download URL. Expires at DateExpires.</summary>
    public string ModioBinaryUrl { get; set; } = "";

    /// <summary>Unix timestamp when ModioBinaryUrl expires.</summary>
    public long   DateExpires   { get; set; }

    public bool IsExpired =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= DateExpires;
}
