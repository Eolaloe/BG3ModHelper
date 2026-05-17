namespace BG3MM_UpdateHelper.Models;

/// <summary>Mod metadata fetched from the Nexus Mods API.</summary>
public class NexusModData
{
    public int      ModId      { get; set; }
    public string   Name       { get; set; } = "";
    public string   Summary    { get; set; } = "";
    public string   Version    { get; set; } = "";
    public string   ProfileUrl { get; set; } = "";
    public DateTime UpdatedAt  { get; set; }
}

/// <summary>A single file entry returned by the Nexus files endpoint.</summary>
public class NexusModFile
{
    public long     FileId        { get; set; }
    public string   Name          { get; set; } = "";
    public string   Version       { get; set; } = "";
    public string   CategoryName  { get; set; } = "";
    public DateTime UploadedAt    { get; set; }
}

/// <summary>Nexus user info — used to determine Premium status.</summary>
public class NexusUserInfo
{
    public int    UserId    { get; set; }
    public string Name      { get; set; } = "";
    public bool   IsPremium { get; set; }
    public string Email     { get; set; } = "";
}
