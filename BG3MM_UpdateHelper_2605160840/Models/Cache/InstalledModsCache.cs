using BG3MM_UpdateHelper.Models;

namespace BG3MM_UpdateHelper.Models.Cache;

/// <summary>
/// Persisted cache of parsed .pak metadata.
/// Stored at %LOCALAPPDATA%\BG3MM_UpdateHelper\installedmods.json.
/// Key = absolute .pak path; value = cached mod data + file timestamp.
/// </summary>
public class InstalledModsCache
{
    /// <summary>Key: absolute .pak file path.</summary>
    public Dictionary<string, InstalledModCacheEntry> Mods { get; set; } = new();
}

/// <summary>One cache entry for a single .pak file.</summary>
public class InstalledModCacheEntry
{
    /// <summary>
    /// Snapshot of File.GetLastWriteTimeUtc() when the entry was cached.
    /// If the actual file timestamp differs, this entry is stale and must be re-parsed.
    /// </summary>
    public DateTime PakFileModified { get; set; }

    /// <summary>Parsed mod data.</summary>
    public InstalledMod ModData { get; set; } = new();
}
