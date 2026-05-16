namespace BG3MM_UpdateHelper.Models.Cache;

/// <summary>
/// Persists the UUID -> Nexus mod ID mapping discovered via MD5 lookup.
/// Stored at %LOCALAPPDATA%\BG3MM_UpdateHelper\nexusmd5.json.
///
/// This lookup only needs to run once per .pak file — the result is stable
/// as long as the file hasn't changed (same MD5 = same Nexus upload).
/// </summary>
public class NexusMd5Cache
{
    /// <summary>
    /// Key: MD5 hex string of the .pak file.
    /// Value: Nexus mod ID (0 = not found on Nexus).
    /// </summary>
    public Dictionary<string, int> Md5ToNexusModId { get; set; } = new();

    /// <summary>Key: mod UUID. Value: Nexus mod ID.</summary>
    public Dictionary<string, int> UuidToNexusModId { get; set; } = new();
}
