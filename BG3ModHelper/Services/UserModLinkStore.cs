using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BG3ModHelper.Services;

/// <summary>
/// Persists user-supplied mod links: either a Nexus mod ID (enables auto-update checking)
/// or any external URL (Patreon, GitHub, etc. — reference/bookmark only).
///
/// Storage: %LOCALAPPDATA%\BG3ModHelper\user_mod_links.json
/// Format:
/// {
///   "nexus":    { "pakFileNameNoExt": 12345, ... },
///   "external": { "pakFileNameNoExt": "https://...", ... }
/// }
/// Legacy format (flat int dict) is auto-migrated on first load.
/// </summary>
public class UserModLinkStore
{
    private static readonly string FilePath =
        Path.Combine(SettingsStore.GetDataFolder(), "user_mod_links.json");

    // pakFileName (no extension, lowercase) → nexusModId
    private Dictionary<string, int>    _nexusLinks    = new(StringComparer.OrdinalIgnoreCase);
    // pakFileName (no extension, lowercase) → external URL
    private Dictionary<string, string> _externalLinks = new(StringComparer.OrdinalIgnoreCase);

    public UserModLinkStore() => Load();

    // ── Nexus ID links ────────────────────────────────────────────────────────

    /// <summary>Returns the user-supplied NexusModId for this pak, or null if not set.</summary>
    public int? GetNexusModId(string pakFileName)
    {
        var key = NormalizeKey(pakFileName);
        return _nexusLinks.TryGetValue(key, out var id) ? id : (int?)null;
    }

    /// <summary>Saves a manual Nexus mod ID link.</summary>
    public void SetNexusLink(string pakFileName, int nexusModId)
    {
        var key = NormalizeKey(pakFileName);
        _nexusLinks[key] = nexusModId;
        _externalLinks.Remove(key); // mutual exclusion
        Save();
    }

    // ── External URL links ────────────────────────────────────────────────────

    /// <summary>Returns the user-supplied external URL for this pak, or null if not set.</summary>
    public string? GetExternalUrl(string pakFileName)
    {
        var key = NormalizeKey(pakFileName);
        return _externalLinks.TryGetValue(key, out var url) ? url : null;
    }

    /// <summary>Saves a manual external URL link (Patreon, GitHub, etc.).</summary>
    public void SetExternalLink(string pakFileName, string url)
    {
        var key = NormalizeKey(pakFileName);
        _externalLinks[key] = url;
        _nexusLinks.Remove(key); // mutual exclusion
        Save();
    }

    // ── Shared ────────────────────────────────────────────────────────────────

    /// <summary>Removes any manual link (Nexus or external) for this pak.</summary>
    public void RemoveLink(string pakFileName)
    {
        var key = NormalizeKey(pakFileName);
        var changed = _nexusLinks.Remove(key) | _externalLinks.Remove(key);
        if (changed) Save();
    }

    /// <summary>Returns true if any manual link (Nexus or external) exists for this pak.</summary>
    public bool HasAnyLink(string pakFileName)
    {
        var key = NormalizeKey(pakFileName);
        return _nexusLinks.ContainsKey(key) || _externalLinks.ContainsKey(key);
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            var json = File.ReadAllText(FilePath);
            var root = JObject.Parse(json);

            if (root.ContainsKey("nexus") || root.ContainsKey("external"))
            {
                // New format
                var nexus = root["nexus"]?.ToObject<Dictionary<string, int>>();
                var ext   = root["external"]?.ToObject<Dictionary<string, string>>();
                if (nexus != null)
                    _nexusLinks = new Dictionary<string, int>(nexus, StringComparer.OrdinalIgnoreCase);
                if (ext != null)
                    _externalLinks = new Dictionary<string, string>(ext, StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                // Legacy format: flat { "pakName": 12345 } — migrate
                var legacy = root.ToObject<Dictionary<string, int>>();
                if (legacy != null)
                    _nexusLinks = new Dictionary<string, int>(legacy, StringComparer.OrdinalIgnoreCase);
                Save(); // rewrite in new format
                Logger.Info("UserModLinkStore: migrated legacy format to new format.");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"UserModLinkStore.Load: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var payload = new { nexus = _nexusLinks, external = _externalLinks };
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(payload, Formatting.Indented));
        }
        catch (Exception ex)
        {
            Logger.Warn($"UserModLinkStore.Save: {ex.Message}");
        }
    }

    private static string NormalizeKey(string pakFileName)
    {
        var name = pakFileName;
        if (name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name.ToLowerInvariant();
    }
}
