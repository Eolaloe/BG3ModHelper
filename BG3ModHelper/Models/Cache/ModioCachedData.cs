using BG3ModHelper.Models;

namespace BG3ModHelper.Models.Cache;

/// <summary>
/// Persisted mod.io API response cache.
/// Stored at %LOCALAPPDATA%\BG3ModHelper\modiodata.json.
/// Key = mod UUID as known locally.
/// </summary>
public class ModioCachedData
{
    /// <summary>Key: mod UUID (from meta.lsx).</summary>
    public Dictionary<string, ModioModData> Mods { get; set; } = new();

    /// <summary>When the cache was last populated from the API.</summary>
    public DateTime LastUpdated { get; set; } = DateTime.MinValue;

    public bool IsExpired(int expiryHours) =>
        DateTime.UtcNow - LastUpdated > TimeSpan.FromHours(expiryHours);
}
