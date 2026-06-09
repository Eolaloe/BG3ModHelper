using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BG3ModHelper.Services;

/// <summary>
/// Persists user-supplied mod links: either a Nexus mod ID (enables auto-update checking)
/// or any external URL (Patreon, GitHub, etc. — reference/bookmark only).
///
/// Storage: %LOCALAPPDATA%\BG3ModHelper\user_mod_links.json
/// Format (v2):
/// {
///   "nexus":    { "pakFileNameNoExt": { "modId": 12345, "hash": "..." }, ... },
///   "external": { "pakFileNameNoExt": "https://...", ... }
/// }
/// Legacy v1 format (flat int dict) is auto-migrated on first load.
///
/// Hash = file-size (16-char hex) + "-" + MD5(first 64 KB).
/// Used to detect when a pak file has been replaced by a different mod's pak
/// (e.g. user uninstalled mod 111 and installed mod 222 with the same pak filename).
/// </summary>
public class UserModLinkStore
{
    private static readonly string FilePath =
        Path.Combine(SettingsStore.GetDataFolder(), "user_mod_links.json");

    private Dictionary<string, NexusLinkEntry> _nexusLinks    = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string>          _externalLinks = new(StringComparer.OrdinalIgnoreCase);

    public UserModLinkStore() => Load();

    // ── Nexus ID links ────────────────────────────────────────────────────────

    /// <summary>Returns the full link entry for this pak, or null if not set.</summary>
    public NexusLinkEntry? GetNexusLink(string pakFileName)
    {
        var key = NormalizeKey(pakFileName);
        return _nexusLinks.TryGetValue(key, out var entry) ? entry : null;
    }

    /// <summary>Returns the stored NexusModId for this pak, or null if not set.</summary>
    public int? GetNexusModId(string pakFileName)
        => GetNexusLink(pakFileName)?.ModId;

    /// <summary>
    /// Saves a Nexus mod ID link, computing the pak file's quick-hash to detect future
    /// file replacement. Pass pakFilePath = null to store without a hash (legacy compat).
    /// </summary>
    public void SetNexusLink(string pakFileName, int nexusModId, string? pakFilePath = null)
    {
        var key  = NormalizeKey(pakFileName);
        var hash = pakFilePath != null ? ComputeQuickHash(pakFilePath) : "";
        _nexusLinks[key] = new NexusLinkEntry(nexusModId, hash);
        _externalLinks.Remove(key); // mutual exclusion
        Save();
    }

    /// <summary>
    /// Recomputes and stores the quick-hash for an existing link after the pak file
    /// has been updated (e.g. following a successful mod download). No-op if no link exists.
    /// </summary>
    public void RefreshPakHash(string pakFileName, string pakFilePath)
    {
        var key = NormalizeKey(pakFileName);
        if (!_nexusLinks.TryGetValue(key, out var entry)) return;
        var newHash = ComputeQuickHash(pakFilePath);
        if (newHash == entry.PakHash) return; // unchanged
        _nexusLinks[key] = new NexusLinkEntry(entry.ModId, newHash);
        Save();
        Logger.Debug($"UserModLinkStore: refreshed hash for {pakFileName}");
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
        var key     = NormalizeKey(pakFileName);
        var changed = _nexusLinks.Remove(key) | _externalLinks.Remove(key);
        if (changed) Save();
    }

    /// <summary>
    /// Returns all stored Nexus links as (normalizedPakName, entry) pairs.
    /// Keys are without extension and lowercase (same form as the dictionary's internal keys).
    /// </summary>
    public IReadOnlyList<(string PakName, NexusLinkEntry Entry)> GetAllNexusLinks()
        => _nexusLinks.Select(kvp => (kvp.Key, kvp.Value)).ToList();

    /// <summary>Returns true if any manual link (Nexus or external) exists for this pak.</summary>
    public bool HasAnyLink(string pakFileName)
    {
        var key = NormalizeKey(pakFileName);
        return _nexusLinks.ContainsKey(key) || _externalLinks.ContainsKey(key);
    }

    // ── Hash utility (public so UpdateChecker can call it without double-read) ──

    /// <summary>
    /// Computes a quick identity hash for a pak file:
    ///   "{fileSize:x16}-{MD5(first 64 KB)}"
    /// Fast enough to run on every update-check scan for a handful of disambiguated paks.
    /// Returns "" on any I/O error or if the file does not exist.
    /// </summary>
    public static string ComputeQuickHash(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists) return "";

            const int sample = 65536; // 64 KB
            var buf  = new byte[sample];
            int read;
            using (var fs = File.OpenRead(filePath))
                read = fs.Read(buf, 0, sample);

            var hashBytes = MD5.HashData(buf.AsSpan(0, read));
            return $"{info.Length:x16}-{Convert.ToHexString(hashBytes).ToLower()}";
        }
        catch (Exception ex)
        {
            Logger.Debug($"UserModLinkStore.ComputeQuickHash({filePath}): {ex.Message}");
            return "";
        }
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            var json = File.ReadAllText(FilePath);
            var root = JObject.Parse(json);

            if (root["nexus"] is JObject nexusObj)
            {
                foreach (var (k, v) in nexusObj)
                {
                    if (v == null) continue;
                    if (v.Type == JTokenType.Integer)
                    {
                        // v1 format: "pakName": 12345  → migrate, no hash
                        _nexusLinks[k] = new NexusLinkEntry((int)v, "");
                    }
                    else if (v.Type == JTokenType.Object)
                    {
                        // v2 format: "pakName": { "modId": 12345, "hash": "..." }
                        var entry = v.ToObject<NexusLinkEntry>();
                        if (entry != null) _nexusLinks[k] = entry;
                    }
                }
                // If we found any v1 entries, rewrite to v2
                if (root["nexus"]?.Children<JProperty>()
                        .Any(p => p.Value.Type == JTokenType.Integer) == true)
                {
                    Save();
                    Logger.Info("UserModLinkStore: migrated v1 nexus entries to v2 (with hash field).");
                }
            }
            else if (!root.ContainsKey("external"))
            {
                // Oldest legacy: flat { "pakName": 12345 } at root
                foreach (var (k, v) in root)
                    if (v?.Type == JTokenType.Integer)
                        _nexusLinks[k] = new NexusLinkEntry((int)v!, "");
                Save();
                Logger.Info("UserModLinkStore: migrated legacy flat format to v2.");
            }

            if (root["external"]?.ToObject<Dictionary<string, string>>() is { } ext)
                _externalLinks = new Dictionary<string, string>(ext, StringComparer.OrdinalIgnoreCase);
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

/// <summary>Stored Nexus link entry: mod ID + quick-hash of the pak file at link time.</summary>
public sealed class NexusLinkEntry
{
    [JsonProperty("modId")] public int    ModId   { get; set; }
    [JsonProperty("hash")]  public string PakHash { get; set; } = "";

    public NexusLinkEntry() { }
    public NexusLinkEntry(int modId, string pakHash) { ModId = modId; PakHash = pakHash; }

}
