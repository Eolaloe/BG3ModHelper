using BG3MM_UpdateHelper.Models;

namespace BG3MM_UpdateHelper.Models.Cache;

/// <summary>
/// Persisted Nexus API response cache.
/// Stored at %LOCALAPPDATA%\BG3MM_UpdateHelper\nexusdata.json.
/// Key = mod UUID as known locally.
/// </summary>
public class NexusCachedData
{
    /// <summary>Key: mod UUID (from meta.lsx).</summary>
    public Dictionary<string, NexusModData> Mods { get; set; } = new();

    /// <summary>When the cache was last populated from the API.</summary>
    public DateTime LastUpdated { get; set; } = DateTime.MinValue;

    public bool IsExpired(int expiryHours) =>
        DateTime.UtcNow - LastUpdated > TimeSpan.FromHours(expiryHours);
}
