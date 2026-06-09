namespace BG3ModHelper.Models;

/// <summary>
/// A candidate Nexus mod/file entry surfaced when multiple mods share the same
/// pak filename. The user picks one to resolve the ambiguity.
/// </summary>
public record NexusModCandidate(
    int    ModId,
    string ModName,
    string Author,
    long   FileId,
    string FileName,
    string FileVersion)
{
    public string PageUrl =>
        $"https://www.nexusmods.com/baldursgate3/mods/{ModId}";

    public string FilesTabUrl =>
        $"https://www.nexusmods.com/baldursgate3/mods/{ModId}?tab=files";

    /// <summary>One-line display: "ModName — FileName (vFileVersion)"</summary>
    public string DisplayLine =>
        string.IsNullOrEmpty(FileName)
            ? $"{ModName}  (mod {ModId})"
            : $"{ModName}  —  {FileName}  v{FileVersion}";
}
