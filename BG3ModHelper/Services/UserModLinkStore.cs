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
/// Format (v3):
/// {
///   "nexus": {
///     "Mod.pak": {
///       "uuid": "abc-123-...", "modId": 12345, "fileId": 67890,
///       "fileName": "Main File", "hash": "..."
///     }, ...
///   },
///   "external": { "Mod.pak": "https://...", ... }
/// }
///
/// Key: pakFileName always WITH .pak extension, lowercase.
/// Legacy entries (v1/v2, without .pak extension) are migrated on first load.
///
/// Hash = file-size (16-char hex) + "-" + MD5(first 64 KB).
/// Used to detect when a pak file has been replaced by a different mod's pak.
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
    /// Saves a complete Nexus match record for this pak.
    ///   uuid:          the pak's MetaUuid — stable identity across pak renames
    ///   fileId:        the specific Nexus file variant chosen (0 = not yet known)
    ///   nexusFileName: the Nexus file slot / variant track name
    ///   pakFilePath:   used to compute a quick-hash for file-replacement detection;
    ///                  if null, preserves the existing hash (or stores empty for new entries)
    /// </summary>
    public void SetNexusLink(
        string  pakFileName,
        int     nexusModId,
        string  uuid          = "",
        long    fileId        = 0,
        string  nexusFileName = "",
        string? pakFilePath   = null)
    {
        var key = NormalizeKey(pakFileName);

        // Preserve existing hash when no path supplied (e.g. link dialog without file access)
        var hash = pakFilePath != null
            ? ComputeQuickHash(pakFilePath)
            : (_nexusLinks.TryGetValue(key, out var prev) ? prev.PakHash : "");

        _nexusLinks[key] = new NexusLinkEntry(uuid, nexusModId, fileId, nexusFileName, hash);
        _externalLinks.Remove(key); // mutual exclusion
        Save();
    }

    /// <summary>
    /// Recomputes and stores the quick-hash after the pak file has been updated
    /// (e.g. following a successful download). No-op if no link exists.
    /// </summary>
    public void RefreshPakHash(string pakFileName, string pakFilePath)
    {
        var key = NormalizeKey(pakFileName);
        if (!_nexusLinks.TryGetValue(key, out var entry)) return;
        var newHash = ComputeQuickHash(pakFilePath);
        if (newHash == entry.PakHash) return;
        _nexusLinks[key] = new NexusLinkEntry(entry.Uuid, entry.ModId, entry.FileId, entry.NexusFileName, newHash);
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
    /// Returns all stored Nexus links as (pakFileName, entry) pairs.
    /// pakFileName always includes the .pak extension.
    /// </summary>
    public IReadOnlyList<(string PakFileName, NexusLinkEntry Entry)> GetAllNexusLinks()
        => _nexusLinks.Select(kvp => (kvp.Key, kvp.Value)).ToList();

    /// <summary>
    /// Finds a Nexus link entry by the pak's MetaUuid.
    /// Used for pak-rename recovery: when the pak filename changed but UUID is stable.
    /// Returns (oldPakFileName, entry) if found, null otherwise.
    /// </summary>
    public (string PakFileName, NexusLinkEntry Entry)? FindByUuid(string uuid)
    {
        if (string.IsNullOrEmpty(uuid)) return null;
        foreach (var kvp in _nexusLinks)
            if (string.Equals(kvp.Value.Uuid, uuid, StringComparison.OrdinalIgnoreCase))
                return (kvp.Key, kvp.Value);
        return null;
    }

    /// <summary>
    /// Migrates a Nexus link entry from an old pak filename key to a new one.
    /// Called when a mod author renames the pak file in a new version.
    /// No-op if the old key does not exist or both keys are the same.
    /// </summary>
    public void RekeyEntry(string oldPakFileName, string newPakFileName)
    {
        var oldKey = NormalizeKey(oldPakFileName);
        var newKey = NormalizeKey(newPakFileName);
        if (string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase)) return;
        if (!_nexusLinks.TryGetValue(oldKey, out var entry)) return;
        _nexusLinks.Remove(oldKey);
        _nexusLinks[newKey] = entry;
        Save();
        Logger.Info($"UserModLinkStore: migrated key [{oldPakFileName}] → [{newPakFileName}] (pak renamed)");
    }

    /// <summary>
    /// Removes all Nexus links and saves. Returns the number of entries deleted.
    /// </summary>
    public int ClearAllNexusLinks()
    {
        int count = _nexusLinks.Count;
        _nexusLinks.Clear();
        if (count > 0) Save();
        return count;
    }

    /// <summary>
    /// Removes all Nexus links whose mod ID matches <paramref name="nexusModId"/>.
    /// Returns the number of entries deleted.
    /// </summary>
    public int RemoveLinksByModId(int nexusModId)
    {
        var toRemove = _nexusLinks
            .Where(kvp => kvp.Value.ModId == nexusModId)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in toRemove)
            _nexusLinks.Remove(key);
        if (toRemove.Count > 0) Save();
        return toRemove.Count;
    }

    /// <summary>Returns true if any manual link (Nexus or external) exists for this pak.</summary>
    public bool HasAnyLink(string pakFileName)
    {
        var key = NormalizeKey(pakFileName);
        return _nexusLinks.ContainsKey(key) || _externalLinks.ContainsKey(key);
    }

    // ── Hash utility ──────────────────────────────────────────────────────────

    /// <summary>
    /// Computes a quick identity hash for a pak file:
    ///   "{fileSize:x16}-{MD5(first 64 KB)}"
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
            bool needsResave = false;

            if (root["nexus"] is JObject nexusObj)
            {
                foreach (var (k, v) in nexusObj)
                {
                    if (v == null) continue;

                    // Normalize key: ensure .pak suffix (legacy entries lacked it)
                    var key = NormalizeKey(k);
                    if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                        needsResave = true;

                    if (v.Type == JTokenType.Integer)
                    {
                        // v1 legacy: "pakName": 12345 (no hash, no uuid, no fileId)
                        _nexusLinks[key] = new NexusLinkEntry("", (int)v, 0, "", "");
                        needsResave = true;
                    }
                    else if (v.Type == JTokenType.Object)
                    {
                        // v2/v3 format: "pakName": { "modId": X, "hash": "..." }
                        // New fields (uuid, fileId, nexusFileName) default to empty/0 if absent.
                        // Migration: old "fileName" key → "nexusFileName"
                        var entry = v.ToObject<NexusLinkEntry>();
                        if (entry != null)
                        {
                            if (string.IsNullOrEmpty(entry.NexusFileName) &&
                                v["fileName"]?.Value<string>() is { Length: > 0 } legacyName)
                            {
                                entry.NexusFileName = legacyName;
                                needsResave = true;
                            }
                            _nexusLinks[key] = entry;
                        }
                    }
                }
            }
            else if (!root.ContainsKey("external"))
            {
                // Oldest legacy: flat { "pakName": 12345 } at root
                foreach (var (k, v) in root)
                {
                    if (v?.Type != JTokenType.Integer) continue;
                    _nexusLinks[NormalizeKey(k)] = new NexusLinkEntry("", (int)v!, 0, "", "");
                }
                needsResave = true;
            }

            if (root["external"]?.ToObject<Dictionary<string, string>>() is { } ext)
            {
                foreach (var (k, url) in ext)
                {
                    var key = NormalizeKey(k);
                    _externalLinks[key] = url;
                    if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                        needsResave = true;
                }
            }

            if (needsResave)
            {
                Save();
                Logger.Info("UserModLinkStore: migrated legacy entries (normalized .pak keys / updated format).");
            }
            Logger.Info($"UserModLinkStore: loaded {_nexusLinks.Count} nexus, {_externalLinks.Count} external entries");
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
            FileHelper.WriteAllTextAtomic(FilePath, JsonConvert.SerializeObject(payload, Formatting.Indented));
        }
        catch (Exception ex)
        {
            Logger.Warn($"UserModLinkStore.Save: {ex.Message}");
        }
    }

    /// <summary>
    /// Normalizes a pak file name to a consistent dictionary key:
    /// trims whitespace, ensures .pak extension, lowercase.
    /// Handles both "ModName" and "ModName.pak" inputs.
    /// </summary>
    private static string NormalizeKey(string pakFileName)
    {
        var name = pakFileName.Trim();
        if (!name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            name += ".pak";
        return name.ToLowerInvariant();
    }
}

/// <summary>
/// Complete match record linking a local pak file to its Nexus counterpart.
/// Written when the user explicitly identifies a mod via the Identify or Change flow.
/// </summary>
public sealed class NexusLinkEntry
{
    /// <summary>MetaUuid from the pak's meta.lsx — stable across version updates and pak renames.</summary>
    [JsonProperty("uuid")]     public string Uuid          { get; set; } = "";

    [JsonProperty("modId")]    public int    ModId         { get; set; }

    /// <summary>Specific Nexus file variant chosen by the user (0 = not yet known).</summary>
    [JsonProperty("fileId")]   public long   FileId        { get; set; }

    /// <summary>Nexus file slot name — identifies the variant track (e.g. "SGT Foxey Lady", "Main File").</summary>
    [JsonProperty("nexusFileName")] public string NexusFileName { get; set; } = "";

    /// <summary>Quick-hash of the pak file at link time — detects if the pak was replaced by a different mod.</summary>
    [JsonProperty("hash")]     public string PakHash       { get; set; } = "";

    public NexusLinkEntry() { }
    public NexusLinkEntry(string uuid, int modId, long fileId, string nexusFileName, string pakHash)
    {
        Uuid          = uuid;
        ModId         = modId;
        FileId        = fileId;
        NexusFileName = nexusFileName;
        PakHash       = pakHash;
    }
}
